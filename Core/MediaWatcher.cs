using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MacDock.Core;

/// <summary>
/// 媒体会话（SMTC）监听：**事件驱动，零轮询**。
/// 只有在"会话出现/消失、媒体信息变、播放状态变"时才回调一次，空闲时完全不做事，
/// 所以对大型游戏没有额外负载。
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    readonly Dispatcher _dispatcher;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _disposed;

    public MediaWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>状态有变化时触发（在 UI 线程上）。</summary>
    public event Action? Changed;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public BitmapSource? Art { get; private set; }
    public bool HasSession => _session is not null;

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(new Action(AttachCurrentSession));
            AttachCurrentSession();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting the media watcher");
        }
    }

    void AttachCurrentSession()
    {
        if (_disposed || _manager is null) return;
        try
        {
            Detach();
            _session = _manager.GetCurrentSession();
            if (_session is null)
            {
                Title = Artist = AppId = "";
                IsPlaying = false;
                Art = null;
                Changed?.Invoke();
                return;
            }

            AppId = _session.SourceAppUserModelId ?? "";
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            ReadPlaybackState();
            _ = ReadMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Attaching to the media session");
        }
    }

    void Detach()
    {
        if (_session is null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch (Exception) { }
        _session = null;
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = ReadMediaPropertiesAsync();
    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        ReadPlaybackState();
        Changed?.Invoke();
    }

    void ReadPlaybackState()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception) { }
    }

    async Task ReadMediaPropertiesAsync()
    {
        try
        {
            var session = _session;
            if (session is null) return;
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || session != _session) return;

            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            if (string.IsNullOrWhiteSpace(Artist)) Artist = props?.AlbumArtist ?? "";

            Art = await LoadArtAsync(props?.Thumbnail);
            _dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
        }
        catch (Exception)
        {
            // 媒体会话随时可能消失，读失败不算错误
        }
    }

    static async Task<BitmapSource?> LoadArtAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream is null || stream.Size == 0) return null;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream.AsStreamForRead();
            bitmap.DecodePixelWidth = 96;     // 卡片里只用到 56px，解码小图省内存
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── 控制（都是一次性调用，不轮询）────────────────────────────────────
    public void PlayPause() => Fire(s => s.TryTogglePlayPauseAsync());
    public void Next() => Fire(s => s.TrySkipNextAsync());
    public void Previous() => Fire(s => s.TrySkipPreviousAsync());

    void Fire(Func<GlobalSystemMediaTransportControlsSession, object> action)
    {
        try
        {
            var session = _session;
            if (session is null) return;
            _ = action(session);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sending a media command");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
        _manager = null;
    }
}
