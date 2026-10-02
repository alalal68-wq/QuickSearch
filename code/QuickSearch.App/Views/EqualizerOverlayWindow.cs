using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickSearch.App.Services;
using QuickSearch.Core.Settings;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace QuickSearch.App.Views;

/// <summary>
/// Decoration-only equalizer: just the audio bars, no search box, no results, no
/// buttons. Meant to live on screen permanently, so unlike the search window it
/// never hides on deactivation and never takes focus or clicks.
///
/// Two placement modes:
///  - Desktop: the window is re-parented to Explorer's wallpaper-level WorkerW
///    (the classic Rainmeter trick: Progman message 0x052C spawns a WorkerW behind
///    the icon layer), so the bars render behind the desktop icons — wallpaper decor.
///  - Topmost: the window floats above every window and is click-through, so it
///    decorates the screen without ever being in the way.
/// </summary>
public sealed class EqualizerOverlayWindow : Window, IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint PROGMAN_SPAWN_WORKERW = 0x052C;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;

    private const double StripHeight = 44;
    private const double ScreenMargin = 8;
    private const double HeightMultiplier = 28.0;

    private readonly Border _strip = new();
    private readonly StackPanel _barsPanel = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly List<Border> _bars = new();
    private readonly DispatcherTimer _audioTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly DispatcherTimer _pinTimer;

    private AudioLevelMonitor? _monitor;
    private IntPtr _workerW;
    private string _mode = "Desktop";
    private double _builtInnerWidth = -1;
    private bool _spawnedWorkerW;
    private bool _disposed;

    public EqualizerOverlayWindow()
    {
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Title = "QuickSearch Equalizer";
        Height = StripHeight;

        _strip.CornerRadius = new CornerRadius(14);
        _strip.Background = new BrushConverter().ConvertFromString("#B80F131C") as Brush;
        _strip.BorderBrush = new BrushConverter().ConvertFromString("#33354558") as Brush;
        _strip.BorderThickness = new Thickness(1);
        _strip.Padding = new Thickness(8, 6, 8, 6);
        _strip.Child = _barsPanel;
        Content = _strip;

        _audioTimer.Tick += (_, _) => PaintBars();

        // Explorer can recreate the wallpaper WorkerW (theme change, explorer
        // restart, resolution change) — check periodically and re-pin if the
        // decoration got orphaned, so it never silently disappears.
        _pinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pinTimer.Tick += (_, _) => EnsurePinned();

        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Applies the overlay-related settings; safe to call repeatedly.</summary>
    public void ApplySettings(AppSettings? settings)
    {
        if (_disposed)
        {
            Trace("ApplySettings skipped: disposed");
            return;
        }

        if (settings == null || !settings.EqualizerOverlayEnabled)
        {
            Trace("ApplySettings: overlay disabled in settings -> hide");
            HideOverlay();
            return;
        }

        var newMode = string.Equals(settings.EqualizerOverlayMode, "Topmost", StringComparison.OrdinalIgnoreCase)
            ? "Topmost"
            : "Desktop";
        var modeChanged = newMode != _mode;
        _mode = newMode;

        var workArea = SystemParameters.WorkArea;
        var widthPercent = Math.Clamp(settings.EqualizerOverlayWidthPercent, 30, 100);
        Width = Math.Max(280, workArea.Width * widthPercent / 100.0);

        ConfigureBars(settings);

        // The native handle must exist before any pinning/style work.
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var wasPinned = IsPinnedToDesktop();

        // Switching between wallpaper-level and top-level placement never works
        // in place: a window that was a child of WorkerW stays buried (SetWindowPos
        // z-order calls silently fail on it). The reliable sequence is: detach,
        // hide+show so WPF rebuilds its window state, then place at the target level.
        if (wasPinned)
            UnpinFromDesktop();

        if (wasPinned || modeChanged || !IsVisible)
        {
            Trace($"ApplySettings: re-show cycle (wasPinned={wasPinned} modeChanged={modeChanged} visible={IsVisible})");
            Hide();
            Show();
        }

        // Hide/Show can recreate the native window — refresh before native calls.
        hwnd = new WindowInteropHelper(this).Handle;

        ApplyExtendedStyle(clickThrough: _mode == "Topmost");

        if (_mode == "Topmost")
        {
            // Reset before set, so WPF actually re-issues the z-order change even
            // if it believes the flag is already true.
            Topmost = false;
            Topmost = true;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _pinTimer.Start(); // watchdog: re-asserts visibility + topmost
        }
        else
        {
            Topmost = false;
            _pinTimer.Start();
        }

        Reposition();

        if (_mode == "Desktop")
        {
            PinToDesktop();
            Reposition(); // parent-relative now; WorkerW spans the screen, so same numbers
        }

        Trace($"ApplySettings done: mode={_mode} shown={IsVisible} left={Left:F0} top={Top:F0} parent=0x{CurrentDesktopParent().ToInt64():X}");
    }

    private void HideOverlay()
    {
        _pinTimer.Stop();
        _audioTimer.Stop();
        _monitor?.Stop();
        UnpinFromDesktop();
        Hide();
    }

    private void ConfigureBars(AppSettings settings)
    {
        var color = Colors.DeepSkyBlue;
        try { color = (Color)ColorConverter.ConvertFromString(settings.AudioBarColor); } catch { }
        var brush = new SolidColorBrush(color);
        var count = Math.Clamp(settings.AudioBarCount, 12, 64);

        // Rebuild only when shape, color or width actually changed, so toggling an
        // unrelated setting doesn't reset the running monitor every time.
        if (_bars.Count == count &&
            Equals((_bars[0].Background as SolidColorBrush)?.Color, color) &&
            Math.Abs(_builtInnerWidth - InnerWidth()) < 1)
            return;

        _builtInnerWidth = InnerWidth();
        _barsPanel.Children.Clear();
        _bars.Clear();

        // Bars span the full inner width of the strip, same layout as the
        // search window's strip so both look identical.
        var innerWidth = _builtInnerWidth;
        var barSpacing = 2;
        var barWidth = Math.Max(2, (innerWidth - (count - 1) * barSpacing) / count);
        _barsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;

        for (var i = 0; i < count; i++)
        {
            var bar = new Border
            {
                Width = barWidth,
                Height = 4,
                Margin = new Thickness(barSpacing / 2.0, 0, barSpacing / 2.0, 0),
                CornerRadius = new CornerRadius(2),
                Background = brush,
                Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _bars.Add(bar);
            _barsPanel.Children.Add(bar);
        }

        _monitor?.Dispose();
        _monitor = new AudioLevelMonitor(count);
        _monitor.Start();
        _audioTimer.Start();
    }

    private void PaintBars()
    {
        if (_monitor == null) return;
        var levels = _monitor.Snapshot();

        var silent = true;
        for (var i = 0; i < levels.Length; i++)
        {
            if (levels[i] > 0.02f)
            {
                silent = false;
                break;
            }
        }

        var now = Environment.TickCount64 / 1000.0;

        for (var i = 0; i < _bars.Count && i < levels.Length; i++)
        {
            if (silent)
            {
                // Idle "breathing" wave: in silence a decorative equalizer still
                // reads as an equalizer — a flat row of dots looks broken.
                var phase = i / (double)_bars.Count * Math.PI * 2;
                var level = 0.14 + 0.10 * (0.5 + 0.5 * Math.Sin(now * 1.7 + phase));
                _bars[i].Height = 4 + level * HeightMultiplier;
            }
            else
            {
                _bars[i].Height = 4 + levels[i] * HeightMultiplier;
            }
        }
    }

    private double InnerWidth() => Width - _strip.Padding.Left - _strip.Padding.Right - 2;

    /// <summary>
    /// True while the window is a child of the wallpaper WorkerW. GetParent() lies
    /// for popup-style re-parented windows (it keeps returning NULL); only
    /// GetAncestor(GA_PARENT) reflects the real parent — every pin check must go
    /// through here.
    /// </summary>
    private bool IsPinnedToDesktop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd != IntPtr.Zero && _workerW != IntPtr.Zero && IsWindow(_workerW) &&
               GetAncestor(hwnd, GA_PARENT) == _workerW;
    }

    private IntPtr CurrentDesktopParent()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_PARENT);
    }

    /// <summary>Temporary diagnostic trail while the overlay feature is being stabilized.</summary>
    private static void Trace(string message)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QuickSearch", "overlay-debug.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // Diagnostics must never break the feature itself.
        }
    }

    // ----- placement -----

    private void Reposition()
    {
        var workArea = SystemParameters.WorkArea;
        var hwnd = new WindowInteropHelper(this).Handle;
        var pinned = IsPinnedToDesktop();

        if (pinned)
        {
            // WPF's Left/Top setters re-issue top-level SetWindowPos calls that can
            // shake the window out of its child-of-WorkerW state; move it natively
            // with parent-relative coordinates instead.
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)(workArea.Left + (workArea.Width - Width) / 2),
                (int)(workArea.Bottom - StripHeight - ScreenMargin),
                0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER);
        }
        else
        {
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = workArea.Bottom - StripHeight - ScreenMargin;
        }
    }

    private void OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea) && IsVisible)
            Reposition();
    }

    private void ApplyExtendedStyle(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (clickThrough)
            exStyle |= WS_EX_TRANSPARENT;
        else
            exStyle &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Re-parents the window to Explorer's wallpaper-level WorkerW so the bars sit
    /// behind the desktop icons, right above the wallpaper.
    /// </summary>
    private void PinToDesktop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Trace("PinToDesktop: no hwnd");
            return;
        }

        if (IsPinnedToDesktop())
            return; // already pinned correctly

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Trace("PinToDesktop: Progman not found");
            return;
        }

        if (!_spawnedWorkerW)
        {
            // Ask Progman to spawn its wallpaper-level WorkerW (it hides behind
            // the icon layer). Extra spawns would only add more candidates.
            SendMessage(progman, PROGMAN_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);
            _spawnedWorkerW = true;
        }

        // Collect every top-level WorkerW. Leftover WorkerWs from earlier spawns
        // may be dead weight that rejects SetParent, so the class lookup is not
        // enough — the only reliable test is trying and re-reading the parent.
        var candidates = new List<IntPtr>();
        EnumWindows((topLevel, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(topLevel, sb, 64);
            if (sb.ToString() == "WorkerW")
                candidates.Add(topLevel);
            return true;
        }, IntPtr.Zero);

        // The classic lookup result first (the WorkerW right after the icon host),
        // then every other WorkerW as fallback.
        IntPtr preferred = IntPtr.Zero;
        EnumWindows((topLevel, _) =>
        {
            if (FindWindowEx(topLevel, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                preferred = FindWindowEx(IntPtr.Zero, topLevel, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        if (preferred != IntPtr.Zero)
            candidates.Remove(preferred);
        candidates.Insert(0, preferred);

        foreach (var workerW in candidates)
        {
            if (workerW == IntPtr.Zero || !IsWindow(workerW))
                continue;

            SetParent(hwnd, workerW);
            var parentNow = GetAncestor(hwnd, GA_PARENT);
            if (parentNow == workerW)
            {
                _workerW = workerW;
                Trace($"PinToDesktop: pinned to 0x{workerW.ToInt64():X}");
                return;
            }

            var err = Marshal.GetLastWin32Error();
            Trace($"PinToDesktop: worker 0x{workerW.ToInt64():X} rejected (parent=0x{parentNow.ToInt64():X} err={err})");
        }

        Trace($"PinToDesktop: no WorkerW accepted the pin ({candidates.Count} tried)");
        // Next timer tick retries; a fresh spawn attempt then helps if Explorer
        // recreated its desktop tree since.
        _spawnedWorkerW = false;
    }

    private void UnpinFromDesktop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Unconditionally: GetParent() reads NULL for popup-style re-parented
        // windows, so checking "is it pinned?" here would skip the detach and
        // leave the window buried behind the desktop forever.
        SetParent(hwnd, IntPtr.Zero);
        _workerW = IntPtr.Zero;
    }

    private void EnsurePinned()
    {
        if (_disposed || !IsVisible)
            return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (_mode == "Topmost")
        {
            // Watchdog: another topmost window (or a shell refresh) may have pushed
            // the strip down — put it back on top without stealing focus.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return;
        }

        if (!IsPinnedToDesktop())
        {
            Trace($"EnsurePinned: re-pinning (parent=0x{CurrentDesktopParent().ToInt64():X} workerW=0x{_workerW.ToInt64():X})");
            PinToDesktop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pinTimer.Stop();
        _audioTimer.Stop();
        _monitor?.Dispose();
        SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;

        // Hide first (no flash), then destroy. A window that was a child of the
        // wallpaper WorkerW must never be reused for another mode — it is thrown
        // away and rebuilt from scratch instead.
        try { Hide(); } catch { }
        try { Close(); } catch { }
    }

    // ----- native interop -----

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr afterChild, string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    private const uint GA_PARENT = 1;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
