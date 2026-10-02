using Windows.Media.Control;
using Windows.Storage.Streams;

namespace QuickSearch.Core.Media;

public class NowPlayingService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    public event EventHandler<MediaInfo>? MediaChanged;
    public event EventHandler<bool>? PlaybackStateChanged;

    public bool IsEnabled { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            UpdateCurrentSession();
            IsEnabled = true;
        }
        catch
        {
            IsEnabled = false;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession();
    }

    private void UpdateCurrentSession()
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _currentSession = _manager?.GetCurrentSession();

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _ = LoadMediaInfoAsync();
        }
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        try
        {
            await LoadMediaInfoAsync();
        }
        catch
        {
            // Never let an exception escape an `async void` event handler — it would crash the app.
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        var isPlaying = sender.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        PlaybackStateChanged?.Invoke(this, isPlaying);
    }

    private async Task LoadMediaInfoAsync()
    {
        if (_currentSession == null) return;

        try
        {
            var properties = await _currentSession.TryGetMediaPropertiesAsync();
            var playbackInfo = _currentSession.GetPlaybackInfo();

            var mediaInfo = new MediaInfo
            {
                Title = properties.Title,
                Artist = properties.Artist,
                AlbumTitle = properties.AlbumTitle,
                IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                ThumbnailStream = properties.Thumbnail
            };

            MediaChanged?.Invoke(this, mediaInfo);
        }
        catch
        {
            // Ignore errors when media info is not available
        }
    }

    public async Task<byte[]?> GetThumbnailAsync()
    {
        if (_currentSession == null) return null;

        try
        {
            var properties = await _currentSession.TryGetMediaPropertiesAsync();
            if (properties.Thumbnail == null) return null;

            using var stream = await properties.Thumbnail.OpenReadAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public void PlayPause()
    {
        _ = _currentSession?.TryTogglePlayPauseAsync();
    }

    public void Next()
    {
        _ = _currentSession?.TrySkipNextAsync();
    }

    public void Previous()
    {
        _ = _currentSession?.TrySkipPreviousAsync();
    }

    public void Dispose()
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
    }
}

public class MediaInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public bool IsPlaying { get; set; }
    public IRandomAccessStreamReference? ThumbnailStream { get; set; }
}
