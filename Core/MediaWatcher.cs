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

    // ── 进度（SMTC 时间轴，事件驱动）─────────────────────────────────────
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    long _positionReadAt;
    long _anchorAt;

    /// <summary>按 SMTC 时间轴推算当前位置（读一次时间轴 + 本地时钟外推，不轮询系统）。</summary>
    /// <summary>播放位置：SMTC 有真实时间轴时用 SMTC；网易云这类全 0 的用本地锚点推算。</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (!IsPlaying || _anchorAt == 0) return Position;
            var drift = Environment.TickCount64 - _anchorAt;
            if (drift <= 0) return Position;
            var projected = Position + TimeSpan.FromMilliseconds(drift);
            return Duration > TimeSpan.Zero && projected > Duration ? Duration : projected;
        }
    }

    void ReadTimeline()
    {
        try
        {
            var timeline = _session?.GetTimelineProperties();
            if (timeline is null) return;
            // 网易云音乐报告 start=end=pos=00:00:00（实测），这时不能把进度归零，
            // 改用"锚点 + 播放状态"推算：每次播放/暂停切换时把当前推算值锚定下来。
            bool zeroTimeline = timeline.EndTime <= TimeSpan.Zero && timeline.Position <= TimeSpan.Zero;
            if (zeroTimeline)
            {
                if (_anchorAt == 0) { _anchorAt = Environment.TickCount64; }
                _positionReadAt = Environment.TickCount64;
                return;
            }
            Position = timeline.Position;
            _anchorAt = Environment.TickCount64;
            Duration = timeline.EndTime - timeline.StartTime;
            _positionReadAt = Environment.TickCount64;
        }
        catch (Exception) { }
    }

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
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            ReadTimeline();
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
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception) { }
        _session = null;
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = ReadMediaPropertiesAsync();
    void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
{
    ReadTimeline();
}
    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        ReadPlaybackState();
        Changed?.Invoke();
    }

    internal void RefreshTimeline() => ReadTimeline();

    /// <summary>用元数据里的准确时长覆盖 SMTC 的空值（网易云报告 end=0）。</summary>
    public void SetKnownDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero) Duration = duration;
    }

    void ReadPlaybackState()
    {
        try
        {
            // 先把"到现在为止的推算值"落成锚点，再切换状态 —— 这样暂停/继续不会丢时间
            var now = CurrentPosition;
            Position = now;
            _anchorAt = Environment.TickCount64;
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

            if (Title != (props?.Title ?? "")) { Position = TimeSpan.Zero; _anchorAt = Environment.TickCount64; Duration = TimeSpan.Zero; }
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
    public void PlayPause() => Fire(s => s.TryTogglePlayPauseAsync(), "TogglePlayPause");
    public void Next() => Fire(s => s.TrySkipNextAsync(), "SkipNext");
    public void Previous() => Fire(s => s.TrySkipPreviousAsync(), "SkipPrevious");

    void Fire(Func<GlobalSystemMediaTransportControlsSession, object> action, string name = "")
    {
        try
        {
            var session = _session;
            if (session is null) return;
            _ = RunCommand(action(session), name);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sending a media command");
        }
    }

    /// <summary>命令耗时诊断：网易云的 SMTC 命令如果本身很慢，UI 就必须先乐观更新。</summary>
    async Task RunCommand(object pending, string name)
    {
        try
        {
            if (pending is Windows.Foundation.IAsyncOperation<bool> operation) await operation;
        }
        catch (Exception ex) { Log.Error(ex, "Sending media command " + name); }

        // 命令本身是 0~1ms 就返回的（实测），真正的延迟在网易云的状态/元数据事件上：
        // 所以命令结束后主动补两次刷新，别让界面干等事件。
        _ = RefreshSoonAsync();
    }

    async Task RefreshSoonAsync()
    {
        foreach (int delay in new[] { 150, 500 })
        {
            await Task.Delay(delay);
            _dispatcher.BeginInvoke(new Action(() =>
            {
                ReadPlaybackState();
                _ = ReadMediaPropertiesAsync();
                Changed?.Invoke();
            }));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Detach();
        _manager = null;
    }
}
