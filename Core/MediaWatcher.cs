using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MacDock.Core;

/// <summary>
/// SMTC 媒体会话中枢（事件驱动，零轮询；只有 1 秒的位置看门狗 + 15 秒的落盘）。
///
/// 分工：
/// · 本类只负责"取"：会话选择、元数据/封面读取、状态与控制可用性、命令下发与回读；
/// · 位置推进、封顶、单曲循环回绕等**判断**都在 <see cref="PositionTracker"/>（有单测）；
/// · 会话优先级打分在 <see cref="MediaRules"/>（有单测）。
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    readonly Dispatcher _dispatcher;
    readonly PositionTracker _tracker = new();
    readonly NetEaseClock _clock = new();     // 网易云内部播放时钟（内存只读），SMTC 无时间轴时用它
    bool _clockLogged;
    long _songChangedAtMs;
    long _lastPosLogMs;
    double _lastNativeSeconds = -1;
    long _lastNativeAtMs;
    long _zeroSinceMs;
    int _irregularCount;
    long _irregularSinceMs;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    bool _disposed;

    public MediaWatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public event Action? Changed;

    // ── 元数据 ──────────────────────────────────────────────────────────
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public BitmapSource? Art { get; private set; }
    public bool HasSession => _session is not null;
    public bool HasMetadata => _session is not null && !string.IsNullOrWhiteSpace(Title);

    // ── 状态 ────────────────────────────────────────────────────────────
    public enum State { Stopped, Playing, Paused, Buffering }

    public State PlaybackState { get; private set; } = State.Stopped;
    public bool IsPlaying => PlaybackState == State.Playing;
    public bool IsBuffering => PlaybackState == State.Buffering;
    public double Rate => _tracker.Rate;

    // ── 控制可用性 ──────────────────────────────────────────────────────
    public bool CanPlayPause { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanSeek { get; private set; }

    // ── 进度（全部委托给 tracker）───────────────────────────────────────
    public TimeSpan Duration => _tracker.Duration;
    public TimeSpan CurrentPosition => _tracker.Current(Environment.TickCount64);
    public bool PositionKnown => _tracker.Known;
    public bool TimelineIsReal { get; private set; }

    public void SetKnownDuration(TimeSpan duration) => _tracker.SetDuration(duration);

    // ── 封面缓存 ────────────────────────────────────────────────────────
    static readonly Dictionary<string, BitmapSource?> ArtCache = new(StringComparer.OrdinalIgnoreCase);

    // ── 生命周期 ────────────────────────────────────────────────────────
    public async void Start() => await InitAsync();

    /// <summary>睡眠唤醒 / 解锁 / 会话丢失后重建（事件与自愈共用这条路径）。</summary>
    public void Restart() => _ = InitAsync();

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

    // ── 会话选择（规则在 MediaRules）────────────────────────────────────
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

    static string SafeAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try { return session.SourceAppUserModelId ?? ""; }
        catch (Exception) { return ""; }
    }

    string _lastSessionLog = "";

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
            if (_session is null) { ClearState(); Changed?.Invoke(); return; }

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

    void ClearState()
    {
        Title = Artist = Album = AppId = "";
        Art = null;
        PlaybackState = State.Stopped;
        CanPlayPause = CanNext = CanPrevious = CanSeek = false;
        _tracker.Reanchor(TimeSpan.Zero, Environment.TickCount64, TimeSpan.Zero, known: false);
        _tracker.SetPlayback(advancing: false, rate: 1.0, duration: TimeSpan.Zero, Environment.TickCount64);
        _metadataKey = "";
        _lastSessionLog = "";
    }

    // ── 事件 → UI 线程 ──────────────────────────────────────────────────
    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = ReadMetadataAsync();

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(new Action(() => { ReadPlaybackInfo(); Changed?.Invoke(); }));

    void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        => _dispatcher.BeginInvoke(new Action(() => { ReadTimeline(); Changed?.Invoke(); }));

    internal void Refresh() { ReadPlaybackInfo(); ReadTimeline(); }
    internal void RefreshTimeline() => ReadTimeline();

    // ── 播放信息 ────────────────────────────────────────────────────────
    void ReadPlaybackInfo()
    {
        var session = _session;
        if (session is null) return;
        if (!IsSourceAlive(AppId)) return;

        try
        {
            var info = session.GetPlaybackInfo();
            var status = info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            double rate = info?.PlaybackRate ?? 1.0;
            if (rate <= 0.01) rate = 1.0;

            var controls = info?.Controls;
            CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? false;
            CanNext = controls?.IsNextEnabled ?? false;
            CanPrevious = controls?.IsPreviousEnabled ?? false;
            CanSeek = controls?.IsPlaybackPositionEnabled ?? false;

            // 很多播放器（含网易云）不填这些开关：四个全 false 视为"未提供信息"，按可用处理
            if (!CanPlayPause && !CanNext && !CanPrevious && !CanSeek)
                CanPlayPause = CanNext = CanPrevious = true;

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

            bool advancing = PlaybackState is State.Playing or State.Buffering;
            _tracker.SetPlayback(advancing, rate, Duration, Environment.TickCount64);
        }
        catch (Exception) { }
    }

    // ── 时间轴 ──────────────────────────────────────────────────────────
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
                // 网易云：时间轴恒为 0，靠 tracker 外推 + 换歌锚定
                TimelineIsReal = false;
                return;
            }

            TimelineIsReal = true;
            var span = timeline.EndTime - timeline.StartTime;
            _tracker.Reanchor(timeline.Position, Environment.TickCount64,
                              span > TimeSpan.Zero ? span : Duration, known: true);
        }
        catch (Exception) { }
    }

    // ── 元数据 ──────────────────────────────────────────────────────────
    string _metadataKey = "";
    string loggedKey = "";
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

            if (!MediaRules.ShouldApplyMetadata(title))
            {
                Log.Info("Media: metadata empty (loading / VIP / copyright?), keeping previous");
                _dispatcher.BeginInvoke(new Action(() => Changed?.Invoke()));
                return;
            }

            string key = title + "|" + artist + "|" + album;
            bool songChanged = !string.Equals(_metadataKey, key, StringComparison.Ordinal);
            if (songChanged)
            {
                _metadataKey = key;
                var resumed = MediaPositionStore.TryResume(title, artist, Duration);
                bool known = MediaRules.IsPositionKnown(TimelineIsReal, resumed is not null, _firstMetadataApplied);
                _tracker.Reanchor(resumed ?? TimeSpan.Zero, Environment.TickCount64,
                                  resumed is null ? TimeSpan.Zero : Duration, known);
                _firstMetadataApplied = true;
                _songChangedAtMs = Environment.TickCount64;   // 换歌时刻，用于校验新扫到的时钟是否从 0 开始
                _clock.Invalidate();          // 换歌后内部时钟对象可能重建，重新校准
                _lastNativeSeconds = -1;
            }

            var art = await LoadArtCachedAsync(key, props.Thumbnail);

            if (songChanged && !string.Equals(loggedKey, key, StringComparison.Ordinal))
            {
                loggedKey = key;
                Log.Info($"Media: song '{title}' - {artist} (album='{album}' art={(art is null ? "no" : "yes")})");
            }

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
        catch (Exception) { }
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

        if (ArtCache.Count > 120) ArtCache.Clear();
        ArtCache[key] = bitmap;
        return bitmap;
    }

    // ── 控制：先查可用性，命令后回读 ────────────────────────────────────
    public void PlayPause()
    {
        if (!CanPlayPause) { Log.Info("MediaCommand: PlayPause suppressed (control disabled)"); return; }
        Fire(s => s.TryTogglePlayPauseAsync(), "TogglePlayPause");
    }

    public void Next()
    {
        if (!CanNext) { Log.Info("MediaCommand: Next suppressed (control disabled)"); return; }
        Fire(s => s.TrySkipNextAsync(), "SkipNext");
    }

    public void Previous()
    {
        if (!CanPrevious) { Log.Info("MediaCommand: Previous suppressed (control disabled)"); return; }
        Fire(s => s.TrySkipPreviousAsync(), "SkipPrevious");
    }

    public void Seek(TimeSpan position)
    {
        if (!CanSeek) { Log.Info("MediaCommand: Seek suppressed (control disabled)"); return; }
        var session = _session;
        if (session is null) return;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        try
        {
            _ = RunCommand(session.TryChangePlaybackPositionAsync(position.Ticks), "Seek");
            _tracker.Reanchor(position, Environment.TickCount64, Duration, known: true);
            _clock.Invalidate();
        }
        catch (Exception ex) { Log.Error(ex, "Seeking"); }
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
        catch (Exception ex) { Log.Error(ex, "Sending media command " + name); }
        await RefreshAfterCommandAsync(name);
    }

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

    // ── 看门狗：位置循环检测 / 会话存活 / 自愈 ──────────────────────────
    System.Windows.Threading.DispatcherTimer? _positionTimer;
    int _noSessionTicks;
    long _lastSelfHealMs;

    void StartPositionWatch()
    {
        if (_positionTimer is not null) return;
        _positionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // 调试：MACDOCK_FAKE_DURATION=秒 时把时长伪装成这个值，
        // 用来在真实播放中触发"贴顶→单曲循环回绕"这条生产代码路径（默认不生效）
        double fakeDurationSeconds = 0;
        if (double.TryParse(Environment.GetEnvironmentVariable("MACDOCK_FAKE_DURATION"), out double parsed)
            && parsed > 0)
            fakeDurationSeconds = parsed;

        _positionTimer.Tick += (_, _) =>
        {
            if (fakeDurationSeconds > 0) _tracker.SetDuration(TimeSpan.FromSeconds(fakeDurationSeconds));

            try
            {
            if (_session is null)
            {
                _noSessionTicks++;
                var now = Environment.TickCount64;
                if (_noSessionTicks >= 10 && now - _lastSelfHealMs > 30_000)
                {
                    _noSessionTicks = 0;
                    _lastSelfHealMs = now;
                    Log.Info("Media: no session for a while - re-initializing SMTC manager");
                    Restart();
                }
                return;
            }
            _noSessionTicks = 0;

            // 真实时钟优先：命中后位置不再是"外推"，而是网易云自己的数
            if (DockConfig.UseMemoryClock
                && AppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)
                && PlaybackState is State.Playing or State.Buffering)
            {
                if (!_clock.IsReady)
                {
                    // "亲眼看到换歌后的外推值"是可信的独立估计；刚换歌（10 秒内）则以 0 为准
                    double? expected = null;
                    if (_songChangedAtMs != 0 && Environment.TickCount64 - _songChangedAtMs < 600_000)
                        expected = _tracker.Current(Environment.TickCount64).TotalSeconds;
                    _clock.EnsureScan(expected, Duration > TimeSpan.Zero ? Duration.TotalSeconds : null);
                }
                if (_clock.TryGetSeconds(out double nativeSeconds))
                {
                    long nowTick = Environment.TickCount64;

                    // 只接受 0 ~ 24 小时：超出说明读到别的计数器（曾把 TimeSpan 撑爆、整块界面变透明）
                    if (!(nativeSeconds >= 0 && nativeSeconds <= 86_400))
                    {
                        Log.Info($"Media: clock value {nativeSeconds:F0}s out of range - dropping address");
                        _clock.Invalidate();
                        return;
                    }

                    // 超过已知时长太多 → 扫错了计数器
                    if (Duration > TimeSpan.Zero && nativeSeconds > Duration.TotalSeconds + 5)
                    {
                        Log.Info($"Media: clock value {nativeSeconds:F1}s exceeds duration {Duration.TotalSeconds:F1}s - rescan");
                        _clock.Invalidate();
                        return;
                    }

                    // 任何跳变都先跟随（拖动进度条 / 换歌 / 重播都是"跳到新位置"）；
                    // 只有连续 3 次不规则（变化量 ≠ 经过时间）才判定地址失效。
                    double dt = _lastNativeAtMs == 0 ? 0 : (nowTick - _lastNativeAtMs) / 1000.0;
                    if (_lastNativeSeconds >= 0 && dt > 0)
                    {
                        double dv = nativeSeconds - _lastNativeSeconds;
                        if (Math.Abs(dv - dt) > 1.0)
                        {
                            if (nowTick - _irregularSinceMs > 10_000) { _irregularSinceMs = nowTick; _irregularCount = 0; }
                            _irregularCount++;
                            Log.Info($"Media: clock jump {_lastNativeSeconds:F1}s -> {nativeSeconds:F1}s (dt={dt:F2}s, irregular #{_irregularCount})");
                            if (_irregularCount >= 3)
                            {
                                Log.Info("Media: clock keeps jumping irregularly - dropping address and rescanning");
                                _clock.Invalidate();
                                _irregularCount = 0;
                                return;
                            }
                        }
                        else _irregularCount = 0;
                    }

                    // 播放中读数一直是 0 → 地址已失效（播放器重建时钟等），立刻重扫
                    if (nativeSeconds < 1.0)
                    {
                        if (_zeroSinceMs == 0) _zeroSinceMs = nowTick;
                        else if (nowTick - _zeroSinceMs > 3_000)
                        {
                            Log.Info("Media: clock stuck at 0 while playing - dropping address");
                            _zeroSinceMs = 0;
                            _clock.Invalidate();
                            return;
                        }
                    }
                    else _zeroSinceMs = 0;

                    if (!_clockLogged)
                    {
                        _clockLogged = true;
                        Log.Info($"Media: native clock active (pid={_clock.ProcessId} addr=0x{_clock.Address:X})");
                    }
                    _lastNativeSeconds = nativeSeconds;
                    _lastNativeAtMs = nowTick;
                    _tracker.Reanchor(TimeSpan.FromSeconds(nativeSeconds), nowTick, Duration, known: true);
                }
            }

            // 诊断：每 5 秒把"内存时钟 / 面板显示"打一条，便于对齐排错
            if (Environment.TickCount64 - _lastPosLogMs > 5000)
            {
                _lastPosLogMs = Environment.TickCount64;
                double clockNow = -1; try { _clock.TryGetSeconds(out clockNow); } catch (Exception) { }
                Log.Info($"Media: pos clock={clockNow:F1}s ui={CurrentPosition.TotalSeconds:F1}s state={PlaybackState} ready={_clock.IsReady} dur={Duration.TotalSeconds:F0}s");
            }

            if (_tracker.Tick(Environment.TickCount64))
            {
                Log.Info($"Media: single-track loop detected - rewound (loop #{_tracker.LoopRewinds})");
                Changed?.Invoke();
            }
            }
            catch (Exception ex)
            {
                // 看门狗里任何异常都不许把 UI 线程带走（曾经因为 TimeSpan 溢出整块界面变透明）
                Log.Error(ex, "Media position tick");
            }
        };
        _positionTimer.Start();
    }

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

    // ── 位置落盘（重启续算）───────────────────────────────────────────
    System.Windows.Threading.DispatcherTimer? _persistTimer;

    public void StartPersistence()
    {
        if (_persistTimer is not null) return;
        _persistTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _persistTimer.Tick += (_, _) => { if (HasSession) PersistPosition(); };
        _persistTimer.Start();
    }

    public void PersistPosition()
    {
        if (string.IsNullOrWhiteSpace(Title)) return;
        MediaPositionStore.Save(Title, Artist, CurrentPosition, IsPlaying);
    }

    public void Dispose()
    {
        _persistTimer?.Stop();
        _positionTimer?.Stop();
        _livenessTimer?.Stop();
        _disposed = true;
        _clock.Dispose();
        Detach();
        _manager = null;
    }
}
