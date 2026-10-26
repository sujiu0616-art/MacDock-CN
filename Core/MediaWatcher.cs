using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MacDock.Core;

/// <summary>
/// SMTC 媒体会话中枢（事件驱动，零轮询）。
///
/// 设计要点（对应"灵动岛数据层"的验收项）：
/// · 会话选择：优先"正在播放"的会话，其次系统当前会话；网易云/浏览器/其它播放器同时存在时不会串台
/// · 元数据原子更新：标题/歌手/专辑/封面来自**同一次读取**，一起替换，绝不出现"新歌名 + 旧歌手"
/// · 封面缓存：按 标题|歌手|专辑 缓存，同一首歌不重复解码
/// · 时长/进度：SMTC 时间轴非 0 就用真值；为 0（网易云）时按播放速率外推，状态变化时重新对齐
/// · 播放状态：Playing / Paused / Stopped / Buffering 四种区分
/// · 控制：每个命令先看 Controls 是否可用；命令后回读真实状态，不做纯乐观更新
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    readonly Dispatcher _dispatcher;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _disposed;

    public MediaWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>状态有变化时触发（UI 线程）。</summary>
    public event Action? Changed;

    // ── 元数据（整体替换，保证一致）─────────────────────────────────────
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public bool HasSession => _session is not null;
    public bool HasMetadata => _session is not null && !string.IsNullOrWhiteSpace(Title);

    // ── 播放状态 ────────────────────────────────────────────────────────
    public enum State { Stopped, Playing, Paused, Buffering }

    public State PlaybackState { get; private set; } = State.Stopped;
    public bool IsPlaying => PlaybackState == State.Playing;
    public bool IsBuffering => PlaybackState == State.Buffering;

    /// <summary>播放速率（SMTC 不给就按 1.0）。用于推进位置外推。</summary>
    public double Rate { get; private set; } = 1.0;

    // ── 控制可用性 ──────────────────────────────────────────────────────
    public bool CanPlayPause { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanSeek { get; private set; }

    // ── 进度 ────────────────────────────────────────────────────────────
    /// <summary>时间轴报告的位置（锚点）。</summary>
    TimeSpan Position { get; set; }
    public TimeSpan Duration { get; private set; }
    long _anchorAt;
    bool _timelineIsReal;          // SMTC 时间轴是不是真值（网易云为 false）
    bool _positionKnown;           // 位置是否可信：时间轴真值 / 落盘续算 / 亲眼看到换歌

    public bool PositionKnown => _positionKnown;

    /// <summary>当前位置：真值 + 按速率外推（暂停时就是锚点本身）。</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            bool advancing = PlaybackState is State.Playing or State.Buffering;
            return MediaRules.ProjectPosition(Position, _anchorAt, Environment.TickCount64, Rate, Duration, advancing);
        }
    }

    // ── 封面缓存 ────────────────────────────────────────────────────────
    static readonly Dictionary<string, BitmapSource?> ArtCache = new(StringComparer.OrdinalIgnoreCase);

    // ── 生命周期 ────────────────────────────────────────────────────────
    public async void Start() => await InitAsync();

    /// <summary>睡眠唤醒 / 解锁后重新接管（SMTC 管理器可能已经失效）。</summary>
    public void Restart()
    {
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            Detach();
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            StartLivenessWatch();
            StartPositionWatch();
            AttachBestSession();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting the media watcher");
        }
    }

    void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => _dispatcher.BeginInvoke(new Action(AttachBestSession));

    void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _dispatcher.BeginInvoke(new Action(AttachBestSession));

    /// <summary>多个会话并存时挑一个：正在播放 &gt; 系统当前会话 &gt; 第一个有元数据的。</summary>
    GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        try
        {
            var sessions = _manager.GetSessions();
            if (sessions is null || sessions.Count == 0) return null;

            var snapshots = new MediaRules.SessionSnapshot[sessions.Count];
            for (int i = 0; i < sessions.Count; i++)
            {
                var candidate = sessions[i];
                int status = (int)(SafeStatus(candidate) ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed);
                long updated = 0;
                try
                {
                    var timeline = candidate.GetTimelineProperties();
                    if (timeline is not null) updated = timeline.LastUpdatedTime.ToUnixTimeMilliseconds();
                }
                catch (Exception) { }
                snapshots[i] = new MediaRules.SessionSnapshot(SafeAppId(candidate), status, updated);
            }

            int best = MediaRules.PickBestIndex(snapshots);
            return best < 0 ? null : sessions[best];
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Picking a media session");
            return null;
        }
    }

    static GlobalSystemMediaTransportControlsSessionPlaybackStatus? SafeStatus(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.GetPlaybackInfo()?.PlaybackStatus; }
        catch (Exception) { return null; }
    }

    void AttachBestSession()
    {
        if (_disposed) return;
        try
        {
            int sessionCount = 0;
            try { sessionCount = _manager?.GetSessions()?.Count ?? 0; } catch (Exception) { }
            var best = PickSession();
            string logKey = sessionCount + ":" + (best is null ? "none" : SafeAppId(best));
            if (logKey != _lastSessionLog)
            {
                _lastSessionLog = logKey;
                Log.Info($"Media: sessions={sessionCount} picked={(best is null ? "none" : SafeAppId(best))}");
            }
            if (ReferenceEquals(best, _session)) { Refresh(); return; }

            Detach();
            _session = best;
            if (_session is null)
            {
                ClearState();
                Changed?.Invoke();
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

            AppId = SafeAppId(_session);
            Log.Info($"Media: attached app={AppId} status={SafeStatus(_session)}");
            ReadPlaybackInfo();
            ReadTimeline();
            _ = ReadMetadataAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Attaching to the media session");
        }
    }

    static string SafeAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? ""; }
        catch (Exception) { return ""; }
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

    /// <summary>SMTC 会话在源进程退出后仍会残留：按 AUMID 里的 exe 名查进程，查不到就当没有会话。</summary>
    static bool IsSourceAlive(string aumid)
        => MediaRules.IsSourceAlive(aumid, name =>
        {
            try { return System.Diagnostics.Process.GetProcessesByName(name).Length > 0; }
            catch (Exception) { return true; }
        });

    System.Windows.Threading.DispatcherTimer? _livenessTimer;

    void StartLivenessWatch()
    {
        if (_livenessTimer is not null) return;
        _livenessTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _livenessTimer.Tick += (_, _) =>
        {
            if (_session is null) return;
            if (IsSourceAlive(AppId)) return;
            Log.Info("Media: source process is gone (" + AppId + ") - clearing state");
            Detach();
            ClearState();
            Changed?.Invoke();
        };
        _livenessTimer.Start();
    }

    void ClearState()
    {
        Title = Artist = Album = AppId = "";
        Art = null;
        PlaybackState = State.Stopped;
        Position = TimeSpan.Zero;
        Duration = TimeSpan.Zero;
        _anchorAt = 0;
        Rate = 1.0;
        CanPlayPause = CanNext = CanPrevious = CanSeek = false;
        _positionKnown = false;
    }

    // ── 事件 → UI 线程 ──────────────────────────────────────────────────
    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _ = ReadMetadataAsync();

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(new Action(() => { ReadPlaybackInfo(); Changed?.Invoke(); }));

    void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(new Action(() => { ReadTimeline(); Changed?.Invoke(); }));

    /// <summary>对外：强制读一次（卡片打开时用，保证进度新鲜）。</summary>
    internal void Refresh()
    {
        ReadPlaybackInfo();
        ReadTimeline();
    }

    internal void RefreshTimeline() => ReadTimeline();

    // ── 读取：播放信息 ──────────────────────────────────────────────────
    void ReadPlaybackInfo()
    {
        var session = _session;
        if (session is null) return;
        if (!IsSourceAlive(AppId)) return;
        try
        {
            // 先把"到现在为止的推算值"落成锚点，再换状态，避免暂停/继续丢时间
            Position = CurrentPosition;
            _anchorAt = Environment.TickCount64;

            var info = session.GetPlaybackInfo();
            var status = info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            Rate = info?.PlaybackRate ?? 1.0;
            if (Rate <= 0.01) Rate = 1.0;

            var controls = info?.Controls;
            CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? false;
            CanNext = controls?.IsNextEnabled ?? false;
            CanPrevious = controls?.IsPreviousEnabled ?? false;
            CanSeek = controls?.IsPlaybackPositionEnabled ?? false;

            // 很多播放器（含网易云）根本不填这些开关：四个全 false 时按"未提供信息"处理，
            // 否则按钮会被误判为不可用而点不动。实测网易云的 Try* 命令是可以工作的。
            if (!CanPlayPause && !CanNext && !CanPrevious && !CanSeek)
            {
                CanPlayPause = CanNext = CanPrevious = true;
                Log.Info("Media: controls flags empty for " + AppId + " - assuming play/pause/next/prev enabled");
            }

            var previousState = PlaybackState;
            PlaybackState = status switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => State.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => State.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => State.Buffering,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => State.Stopped,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => State.Stopped,
                _ => State.Paused,
            };
            if (previousState != PlaybackState)
                Log.Info($"Media: state {previousState} -> {PlaybackState} (status={status})");
        }
        catch (Exception)
        {
            // 会话随时可能消失
        }
    }

    // ── 读取：时间轴 ────────────────────────────────────────────────────
    void ReadTimeline()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is null) return;

            bool zero = timeline.EndTime <= TimeSpan.Zero && timeline.Position <= TimeSpan.Zero;
            if (zero)
            {
                // 网易云：时间轴恒为 0，用锚点 + 速率外推；真实时长由 lyrics 服务回填
                _timelineIsReal = false;
                if (_anchorAt == 0) _anchorAt = Environment.TickCount64;
                return;
            }

            _timelineIsReal = true;
            Position = timeline.Position;
            var span = timeline.EndTime - timeline.StartTime;
            if (span > TimeSpan.Zero) Duration = span;
            _anchorAt = Environment.TickCount64;
        }
        catch (Exception) { }
    }

    /// <summary>用外部来源（歌词接口）回填准确时长。</summary>
    public void SetKnownDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero && Duration <= TimeSpan.Zero) Duration = duration;
    }

    /// <summary>时间轴是否是 SMTC 真值（false 表示靠外推）。</summary>
    public bool TimelineIsReal => _timelineIsReal;

    // ── 读取：元数据（整体替换 + 封面缓存）─────────────────────────────
    string _metadataKey = "";
    string loggedKey = "";
    string _lastSessionLog = "";
    bool _sawSongChange;
    bool _firstMetadataApplied;

    async Task ReadMetadataAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || !ReferenceEquals(session, _session) || props is null) return;

            string title = props.Title ?? "";
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            string album = props.AlbumTitle ?? "";

            // 加载中/版权受限时字段可能为空：此时保留上一次的完整元数据，等下一次读取，
            // 避免出现"新歌名 + 旧歌手"这种中间态。
            if (!MediaRules.ShouldApplyMetadata(title))
            {
                Log.Info("Media: metadata empty (loading / VIP / copyright?), keeping previous");
                _dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
                return;
            }

            // 换歌：位置归零（或用落盘续算），时长清空等回填
            if (!string.Equals(_metadataKey, title + "|" + artist + "|" + album, StringComparison.Ordinal))
            {
                _metadataKey = title + "|" + artist + "|" + album;
                var resumed = MediaPositionStore.TryResume(title, artist, Duration);
                Position = resumed ?? TimeSpan.Zero;
                _anchorAt = Environment.TickCount64;
                if (resumed is null) Duration = TimeSpan.Zero;
                // 亲眼看到换歌 → 位置从 0 开始，可信；有落盘续算也可信；首次拿到元数据（可能已播一半）仍不可信
                _positionKnown = MediaRules.IsPositionKnown(_timelineIsReal, resumed is not null, _firstMetadataApplied);
                _firstMetadataApplied = true;
            }

            var art = await LoadArtCachedAsync(_metadataKey, props.Thumbnail);
            // 调试开关：想验证"没有封面时的占位表现"时，把 MACDOCK_FORCE_NOART=1 设进环境变量即可
            if (Environment.GetEnvironmentVariable("MACDOCK_FORCE_NOART") == "1") art = null;
            bool isNewSong = !string.Equals(loggedKey, _metadataKey, StringComparison.Ordinal);
            if (isNewSong) Log.Info($"Media: song '{title}' - {artist} (album='{album}' art={(art is null ? "no" : "yes")})");
            loggedKey = _metadataKey;

            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(session, _session)) return;
                Title = title;
                Artist = artist;
                Album = album;
                Art = art;
                Changed?.Invoke();
            }));
        }
        catch (Exception)
        {
            // 会话消失 / VIP 试听等场景读失败：静默，等下一次事件
        }
    }

    static async Task<BitmapSource?> LoadArtCachedAsync(string key, IRandomAccessStreamReference? reference)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (ArtCache.TryGetValue(key, out var cached)) return cached;

        BitmapSource? bitmap = null;
        if (reference is not null)
        {
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream is not null && stream.Size > 0)
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = stream.AsStreamForRead();
                    image.DecodePixelWidth = 96;
                    image.EndInit();
                    image.Freeze();
                    bitmap = image;
                }
            }
            catch (Exception) { }
        }

        if (ArtCache.Count > 120) ArtCache.Clear();   // 简单封顶，别无限涨
        ArtCache[key] = bitmap;
        return bitmap;
    }

    // ── 控制：先查可用性，命令后回读 ────────────────────────────────────
    public void PlayPause()
    {
        if (!CanPlayPause) return;
        Fire(s => s.TryTogglePlayPauseAsync(), "TogglePlayPause");
    }

    public void Next()
    {
        if (!CanNext) return;
        Fire(s => s.TrySkipNextAsync(), "SkipNext");
    }

    public void Previous()
    {
        if (!CanPrevious) return;
        Fire(s => s.TrySkipPreviousAsync(), "SkipPrevious");
    }

    public void Seek(TimeSpan position)
    {
        if (!CanSeek) return;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;

        var session = _session;
        if (session is null) return;
        try
        {
            _ = RunCommand(session.TryChangePlaybackPositionAsync(position.Ticks), "Seek");
            // 本地立即对齐，回读会再校正
            Position = position;
            _anchorAt = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seeking");
        }
    }

    void Fire(Func<GlobalSystemMediaTransportControlsSession, object> action, string name)
    {
        var session = _session;
        if (session is null) return;
        try { _ = RunCommand(action(session), name); }
        catch (Exception ex) { Log.Error(ex, "Sending media command " + name); }
    }

    async Task RunCommand(object pending, string name)
    {
        try
        {
            if (pending is Windows.Foundation.IAsyncOperation<bool> operation) await operation;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sending media command " + name);
        }
        await RefreshAfterCommandAsync(name);
    }

    /// <summary>命令后回读真实状态（两次，覆盖应用回执慢的情况）。</summary>
    async Task RefreshAfterCommandAsync(string name)
    {
        foreach (int delay in new[] { 120, 450 })
        {
            await Task.Delay(delay);
            _dispatcher.BeginInvoke(new Action(() =>
            {
                Refresh();
                _ = ReadMetadataAsync();
                Changed?.Invoke();
                Log.Info($"MediaCommand: {name} readback state={PlaybackState} pos={CurrentPosition.TotalSeconds:F1}s");
            }));
        }
    }

    public void PersistPosition()
    {
        if (string.IsNullOrWhiteSpace(Title)) return;
        MediaPositionStore.Save(Title, Artist, CurrentPosition, IsPlaying);
    }

    long _ceilingSinceMs;
    int _noSessionTicks;        // 连续多少秒没有会话
    long _lastSelfHealMs;       // 上次自愈时间（节流）


    System.Windows.Threading.DispatcherTimer? _positionTimer;

    /// <summary>
    /// 单曲循环检测：网易云的时间轴恒为 0、也没有"换歌"事件（同一首歌重播），
    /// 所以外推会一直停在天花板上。这里每秒看一眼，贴天花板超过 1.5 秒仍在播放就当归零重来。
    /// </summary>
    void StartPositionWatch()
    {
        if (_positionTimer is not null) return;
        _positionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _positionTimer.Tick += (_, _) =>
        {
            if (_session is null)
            {
                // 会话空了：可能是应用刚退出（正常），也可能是睡眠唤醒后 SMTC 管理器失效（需要重建）。
                // 每 10 秒试一次重建，最多每 30 秒一次，避免频繁请求。
                _ceilingSinceMs = 0;
                _noSessionTicks++;
                var tickNow = Environment.TickCount64;
                if (_noSessionTicks >= 10 && tickNow - _lastSelfHealMs > 30_000)
                {
                    _noSessionTicks = 0;
                    _lastSelfHealMs = tickNow;
                    Log.Info("Media: no session for a while - re-initializing SMTC manager");
                    Restart();
                }
                return;
            }
            _noSessionTicks = 0;

            if (!_positionKnown || Duration <= TimeSpan.Zero) { _ceilingSinceMs = 0; return; }
            if (PlaybackState != State.Playing) { _ceilingSinceMs = 0; return; }

            var now = Environment.TickCount64;
            var position = CurrentPosition;
            if (!MediaRules.IsAtCeiling(position, Duration)) { _ceilingSinceMs = 0; return; }

            if (_ceilingSinceMs == 0) { _ceilingSinceMs = now; return; }
            if (!MediaRules.ShouldRestartLoop(_ceilingSinceMs, now)) return;

            Log.Info($"Media: single-track loop detected - rewinding anchor (was {position.TotalSeconds:F0}s of {Duration.TotalSeconds:F0}s)");
            Position = TimeSpan.Zero;
            _anchorAt = now;
            _ceilingSinceMs = 0;
            Changed?.Invoke();
        };
        _positionTimer.Start();
    }

    System.Windows.Threading.DispatcherTimer? _persistTimer;

    public void StartPersistence()
    {
        if (_persistTimer is not null) return;
        _persistTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _persistTimer.Tick += (_, _) => { if (HasSession) PersistPosition(); };
        _persistTimer.Start();
    }

    public void Dispose()
    {
        _persistTimer?.Stop();
        _positionTimer?.Stop();
        _disposed = true;
        Detach();
        _manager = null;
    }
}
