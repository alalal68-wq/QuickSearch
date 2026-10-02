using System.Runtime.InteropServices;

namespace QuickSearch.App.Services;

/// <summary>
/// Listens to the default playback device (speakers or headphones) through WASAPI loopback
/// and folds the signal into a short row of bar heights in the range 0..1.
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly int _barCount;
    private readonly float[] _levels;
    private readonly float[] _targets;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _running;
    private bool _disposed;

    public event EventHandler? LevelsChanged;

    public AudioLevelMonitor(int barCount)
    {
        _barCount = Math.Clamp(barCount, 12, 64);
        _levels = new float[_barCount];
        _targets = new float[_barCount];
    }

    public int BarCount => _barCount;

    public bool IsRunning => _running;

    public float[] Snapshot()
    {
        lock (_gate)
        {
            var copy = new float[_barCount];
            Array.Copy(_levels, copy, _barCount);
            return copy;
        }
    }

    public void Start()
    {
        if (_running || _disposed) return;

        _cts = new CancellationTokenSource();
        _running = true;
        var token = _cts.Token;
        _loop = Task.Run(() => CaptureLoop(token), token);
    }

    public void Stop()
    {
        _running = false;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }

        try { _loop?.Wait(800); }
        catch (AggregateException) { }

        _cts?.Dispose();
        _cts = null;
        _loop = null;

        lock (_gate)
        {
            Array.Clear(_levels);
            Array.Clear(_targets);
        }

        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void CaptureLoop(CancellationToken token)
    {
        LoopbackSession? session = null;

        while (!token.IsCancellationRequested)
        {
            if (session == null || !session.IsAlive)
            {
                session?.Dispose();
                session = LoopbackSession.TryOpen();
                if (session == null)
                {
                    DecayTowardSilence();
                    token.WaitHandle.WaitOne(700);
                    continue;
                }
            }

            try
            {
                if (!session.Read(out var samples, out var channels))
                {
                    token.WaitHandle.WaitOne(30);
                    continue;
                }

                if (samples.Length == 0 || channels <= 0)
                {
                    DecayTowardSilence();
                    continue;
                }

                PushSpectrum(samples, channels);
            }
            catch
            {
                session?.Dispose();
                session = null;
                token.WaitHandle.WaitOne(500);
            }
        }

        session?.Dispose();
    }

    /// <summary>
    /// Splits the interleaved buffer into frequency-ish bands by walking the waveform
    /// at different strides. This is not an FFT; it is enough for a decorative strip
    /// and stays cheap on the UI thread's neighbor.
    /// </summary>
    private void PushSpectrum(float[] interleaved, int channels)
    {
        var frames = interleaved.Length / channels;
        if (frames < 8) return;

        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }

        lock (_gate)
        {
            for (var band = 0; band < _barCount; band++)
            {
                // Low bands look at longer stretches, high bands at short ones,
                // so the row reads left-to-right like a tiny spectrum.
                var window = Math.Max(2, frames / (_barCount - band + 2));
                var start = (band * Math.Max(1, frames - window)) / _barCount;
                double energy = 0;
                var count = 0;
                for (var i = start; i < start + window && i < frames; i++)
                {
                    var s = mono[i];
                    energy += s * s;
                    count++;
                }

                var rms = count == 0 ? 0 : Math.Sqrt(energy / count);
                // Asymptotic curve instead of a hard clamp: quiet passages are still
                // clearly visible, and loud passages approach the top smoothly instead
                // of instantly slamming to 100% (which is what "на фулл" looked like).
                var shaped = 1.0 - Math.Exp(-rms * 15.0);
                _targets[band] = (float)Math.Clamp(shaped, 0, 1);

                var current = _levels[band];
                var follow = _targets[band] > current ? 0.55f : 0.28f;
                _levels[band] = current + (_targets[band] - current) * follow;
            }
        }

        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DecayTowardSilence()
    {
        var changed = false;
        lock (_gate)
        {
            for (var i = 0; i < _barCount; i++)
            {
                if (_levels[i] < 0.01f)
                {
                    _levels[i] = 0;
                    continue;
                }

                _levels[i] *= 0.82f;
                changed = true;
            }
        }

        if (changed)
            LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One open WASAPI loopback client on the default render endpoint.</summary>
    private sealed class LoopbackSession : IDisposable
    {
        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioClient? _client;
        private IAudioCaptureClient? _capture;
        private int _channels;
        private int _bytesPerFrame;
        private bool _floatFormat;
        private bool _alive = true;

        public bool IsAlive => _alive;

        public static LoopbackSession? TryOpen()
        {
            var session = new LoopbackSession();
            try
            {
                session._enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // eRender = 0, eConsole = 0: whatever Windows is actually playing through.
                session._enumerator.GetDefaultAudioEndpoint(0, 0, out session._device);
                session._device.Activate(typeof(IAudioClient).GUID, 1, IntPtr.Zero, out var activated);
                session._client = (IAudioClient)activated;

                var formatPtr = session._client.GetMixFormat();
                try
                {
                    var format = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
                    session._channels = Math.Max(1, (int)format.nChannels);
                    session._bytesPerFrame = Math.Max(1, (int)format.nBlockAlign);
                    session._floatFormat = format.wFormatTag == 3;

                    if (format.wFormatTag == 0xFFFE && format.cbSize >= 22)
                    {
                        // WAVEFORMATEX header is 18 bytes; WAVEFORMATEXTENSIBLE adds
                        // wValidBitsPerSample (2) + dwChannelMask (4) = 6 bytes before
                        // the SubFormat GUID, so the GUID starts at offset 24, not 22.
                        var subtype = Marshal.PtrToStructure<Guid>(formatPtr + 24);
                        // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
                        session._floatFormat = subtype == new Guid("00000003-0000-0010-8000-00aa00389b71");
                    }

                    // AUDCLNT_SHAREMODE_SHARED = 0, AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000
                    session._client.Initialize(0, 0x00020000, 1000000, 0, formatPtr, Guid.Empty);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(formatPtr);
                }

                var iid = typeof(IAudioCaptureClient).GUID;
                session._client.GetService(ref iid, out var captureObj);
                session._capture = (IAudioCaptureClient)captureObj;
                session._client.Start();
                return session;
            }
            catch
            {
                session.Dispose();
                return null;
            }
        }

        public bool Read(out float[] samples, out int channels)
        {
            samples = Array.Empty<float>();
            channels = _channels;
            if (_capture == null) return false;

            var packet = _capture.GetNextPacketSize();
            if (packet == 0) return false;

            var collected = new List<float>(packet * _channels * 4);

            while (packet > 0)
            {
                var hr = _capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                if (hr != 0)
                {
                    // GetBuffer failed (e.g. AUDCLNT_E_DEVICE_INVALIDATED / a device format
                    // change under load). `data`/`frames` are meaningless here — do NOT touch
                    // them and do NOT call ReleaseBuffer, or we risk reading/releasing a bogus
                    // buffer, which is how this used to hard-crash the whole process with an
                    // unrecoverable AccessViolationException (something try/catch can't stop).
                    break;
                }

                try
                {
                    // AUDCLNT_BUFFERFLAGS_SILENT
                    if ((flags & 0x2) != 0 || data == IntPtr.Zero || frames == 0)
                    {
                        for (var i = 0; i < frames * _channels; i++)
                            collected.Add(0);
                    }
                    else
                    {
                        var byteCount = frames * _bytesPerFrame;
                        var raw = new byte[byteCount];
                        Marshal.Copy(data, raw, 0, byteCount);
                        Decode(raw, frames, collected);
                    }
                }
                finally
                {
                    _capture.ReleaseBuffer(frames);
                }

                packet = _capture.GetNextPacketSize();
            }

            samples = collected.ToArray();
            return true;
        }

        private void Decode(byte[] raw, int frames, List<float> collected)
        {
            if (_floatFormat)
            {
                var count = Math.Min(frames * _channels, raw.Length / 4);
                for (var i = 0; i < count; i++)
                    collected.Add(BitConverter.ToSingle(raw, i * 4));
                return;
            }

            // 16-bit PCM fallback.
            var samples = Math.Min(frames * _channels, raw.Length / 2);
            for (var i = 0; i < samples; i++)
            {
                var value = BitConverter.ToInt16(raw, i * 2);
                collected.Add(value / 32768f);
            }
        }

        public void Dispose()
        {
            _alive = false;
            try { _client?.Stop(); } catch { /* endpoint already gone */ }
            Release(ref _capture);
            Release(ref _client);
            Release(ref _device);
            Release(ref _enumerator);
        }

        private static void Release<T>(ref T? com) where T : class
        {
            if (com != null && Marshal.IsComObject(com))
                Marshal.ReleaseComObject(com);
            com = null;
        }
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out IntPtr store);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
        IntPtr format, [MarshalAs(UnmanagedType.LPStruct)] Guid audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out int frames);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    [PreserveSig]
    int GetCurrentPadding(out int padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);

    IntPtr GetMixFormat();

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr handle);

    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig]
    int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(int frames);

    int GetNextPacketSize();
}
