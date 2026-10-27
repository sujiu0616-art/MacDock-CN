using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 灵动岛：菜单栏正中的小胶囊 + 展开后的媒体卡片。
///
/// 性能约束（用户要求"不能影响大型游戏"）：
/// · 收起状态只是一个圆角 Border，无动画、无计时器、无网络；
/// · 展开体是**独立的分层窗口**，不改菜单栏尺寸（菜单栏是 appbar，改尺寸会惊动 Explorer）；
/// · 媒体/进度/歌词全部"只在卡片展开时"才刷新：300ms 的 UI 计时器 + 本地推算，
///   不读系统、不轮询进程；关闭卡片后计时器停止，零开销；
/// · 歌词只在换歌时联网一次，之后落盘缓存。
/// </summary>
internal sealed class IslandWindow : Window
{
    static IslandWindow? _open;

    readonly Border _card;
    readonly Image _art;
    readonly TextBlock _title;
    readonly TextBlock _artist;
    readonly TextBlock _empty;
    readonly TextBlock _lyric;
    readonly TextBlock _elapsed;
    readonly TextBlock _total;
    readonly Border _progressTrack;
    readonly Border _progressFill;
    readonly Grid _mediaGrid;
    readonly ButtonRow _buttons;
    readonly System.Windows.Threading.DispatcherTimer _leaveTimer;
    readonly System.Windows.Threading.DispatcherTimer _ticker;

    MediaWatcher? _media;
    IntPtr _ownerHandle;
    LyricsService.Lyrics? _lyrics;
    string _lyricsKey = "";
    bool _fetchingLyrics;
    int _lyricOffsetMs;
    TextBlock? _artPlaceholder;
    System.Windows.Shapes.Path? _likeGlyph;
    readonly MacDock.Core.NetEaseBridge _bridge = new();
    long _likeResetAtMs;
    bool _heartLiked;
    Grid? _progressHost;
    TextBlock? _stateLabel;
    long _lastPersistAt;
    bool _pointerInside;
    bool? _optimisticPlaying;   // 已弃用：保留字段避免旧代码引用

    static void SetButtonEnabled(Panel buttons, bool enabled)
    {
        if (buttons is null) return;
        buttons.Opacity = enabled ? 1.0 : 0.5;
    }

    public static bool IsOpen => _open is not null;

    /// <summary>三个控制按钮横向排开（之前用 Grid 没加列定义，三个按钮挤在同一格里）。</summary>
    sealed class ButtonRow : StackPanel
    {
        public ButtonRow() { Orientation = Orientation.Horizontal; }
    }

    public IslandWindow()
    {
        Title = "MacDock Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 10;
        Height = 10;
        Left = -32000;
        Top = -32000;
        Cursor = Cursors.Arrow;

        // ── 封面 ───────────────────────────────────────────────────────────
        _art = new Image { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        _artPlaceholder = new TextBlock
        {
            Text = "\u266A",                       // ♪
            FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
        };

        var artStack = new Grid();
        artStack.Children.Add(_artPlaceholder);
        artStack.Children.Add(_art);

        var artFrame = new Border
        {
            Child = artStack,
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(11),
            Background = Frozen(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ── 文字 ───────────────────────────────────────────────────────────
        _title = MakeText("", 13, FontWeights.SemiBold, 168);
        _artist = MakeText("", 11, FontWeights.Normal, 168);
        _lyric = MakeText("", 11.5, FontWeights.Normal, 236);
        _lyric.Margin = new Thickness(0, 6, 0, 0);
        _lyric.Opacity = 0.85;

        var textColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        textColumn.Children.Add(_title);
        textColumn.Children.Add(_artist);

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(artFrame);
        header.Children.Add(textColumn);

        // ── 按钮 ───────────────────────────────────────────────────────────
        _buttons = new ButtonRow { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var prev = GlyphButton("M11,1 L2.6,6 L11,11 Z M0.4,1 L1.8,1 L1.8,11 L0.4,11 Z", () => _media?.Previous());
        var play = GlyphButton(PlayGeometry, () =>
        {
            // 乐观更新：先把图标切过去，事件回来再对齐（网易云的命令回执常有几百毫秒）
            _media?.PlayPause();   // 不做乐观更新：命令后回读真值再刷新
        });
        var next = GlyphButton("M1,1 L9.4,6 L1,11 Z M10.2,1 L11.6,1 L11.6,11 L10.2,11 Z", () => _media?.Next());
        _buttons.Children.Add(CreateLikeButton());
        _buttons.Children.Add(prev);
        _buttons.Children.Add(play);
        _buttons.Children.Add(next);
        _playGlyph = (System.Windows.Shapes.Path)((Border)play).Child;

        // 三段分散对齐：封面贴左、文字占满中间、按钮贴右（左右内边距收紧到 10）
        _buttons.Margin = new Thickness(0, 0, 0, 0);
        var top = new Grid { VerticalAlignment = VerticalAlignment.Center };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(header);
        top.Children.Add(_buttons);
        Grid.SetColumn(_buttons, 2);

        // ── 进度 ───────────────────────────────────────────────────────────
        _progressTrack = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Frozen(Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _progressFill = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };
        var progressHost = new Grid { Margin = new Thickness(0, 8, 0, 0), Background = Brushes.Transparent };
        _progressHost = progressHost;
        progressHost.Children.Add(_progressTrack);
        progressHost.Children.Add(_progressFill);

        _stateLabel = MakeText("", 10, FontWeights.Normal, 120);
        _stateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _stateLabel.Opacity = 0.75;

        _elapsed = MakeText("0:00", 10, FontWeights.Normal, 60);
        _total = MakeText("0:00", 10, FontWeights.Normal, 60);
        _total.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_elapsed, 0);
        Grid.SetColumn(_stateLabel, 1);
        Grid.SetColumn(_total, 2);
        _total.TextAlignment = TextAlignment.Right;
        times.Children.Add(_elapsed);
        times.Children.Add(_stateLabel);
        times.Children.Add(_total);

        // ── 组合 ───────────────────────────────────────────────────────────
        var stack = new StackPanel();
        stack.Children.Add(top);
        _progressTrack.SizeChanged += (_, _) => UpdateProgressAndLyric();

        // 点进度条跳转（网易云是否支持由 SMTC 的 Controls.IsPlaybackPositionEnabled 决定）
        progressHost.MouseLeftButtonDown += (_, e) =>
        {
            var media = _media;
            if (media is null || !media.CanSeek || media.Duration <= TimeSpan.Zero) return;
            double width = progressHost.ActualWidth;
            if (width <= 0) return;
            double ratio = Math.Clamp(e.GetPosition(progressHost).X / width, 0, 1);
            media.Seek(TimeSpan.FromMilliseconds(media.Duration.TotalMilliseconds * ratio));
            e.Handled = true;
        };
        _mediaGrid = new Grid();
        _mediaGrid.Children.Add(stack);

        _empty = MakeText("没有正在播放的媒体", 12, FontWeights.Normal, 260);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(12, 0, 0, 0);

        var content = new Grid();
        content.Children.Add(_empty);
        content.Children.Add(_mediaGrid);

        _card = new Border
        {
            Child = content,
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
        };
        _lyricOffsetMs = DockConfig.LyricOffsetMs;
        Content = _card;
        ApplyTheme();

        _leaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _leaveTimer.Tick += (_, _) => { _leaveTimer.Stop(); HideIsland(); };

        // 只在卡片展开时跑：300ms 更新进度条与当前歌词（纯本地计算）
        _ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _ticker.Tick += (_, _) => UpdateProgressAndLyric();

        MouseEnter += (_, _) => _leaveTimer.Stop();
        MouseLeave += (_, _) => { _leaveTimer.Stop(); _leaveTimer.Start(); };
    }

    System.Windows.Shapes.Path _playGlyph = null!;

    static TextBlock MakeText(string text, double size, FontWeight weight, double maxWidth)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Typo.Ui,
            FontSize = size,
            FontWeight = weight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = maxWidth,
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
        return t;
    }

    internal const string PlayGeometry = "M1,0 L11,6 L1,12 Z";
    internal const string PauseGeometry = "M0.8,0 L4,0 L4,12 L0.8,12 Z M7,0 L10.2,0 L10.2,12 L7,12 Z";

    /// <summary>红心：SMTC 没有收藏接口，走网易云自己的页面（需要它以调试端口启动）。</summary>
    bool _heartKnown;
    string _heartSongKey = "";

    /// <summary>三态：未知（暗）/ 未收藏（白空心）/ 已收藏（实红）。只反映网易云的真实状态。</summary>
    void PaintHeart(bool? liked)
    {
        if (_likeGlyph is null) return;
        var accent = Frozen(Color.FromArgb(0xFF, 0xE0, 0x3B, 0x54));
        var normal = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        var unknown = Frozen(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

        if (liked is null)
        {
            _heartKnown = false;
            _likeGlyph.Fill = Brushes.Transparent;
            _likeGlyph.Stroke = unknown;
            return;
        }
        _heartKnown = true;
        _heartLiked = liked.Value;
        _likeGlyph.Fill = liked.Value ? accent : Brushes.Transparent;
        _likeGlyph.Stroke = liked.Value ? accent : normal;
    }

    DateTime _heartNextProbe = DateTime.MinValue;

    /// <summary>同步真实收藏状态：最多 5 秒一次；没有调试端口就静默跳过。</summary>
    void RefreshHeartState()
    {
        if (DateTime.UtcNow < _heartNextProbe) return;
        _heartNextProbe = DateTime.UtcNow.AddSeconds(4);
        _ = Task.Run(async () =>
        {
            var liked = await _bridge.GetLikeStateAsync();
            Dispatcher.BeginInvoke(new Action(() => PaintHeart(liked)));
        });
    }

    UIElement CreateLikeButton()
    {
        var heart = Geometry.Parse("M12,21 C12,21 3,14.5 3,8.6 3,5.5 5.4,3.2 8.3,3.2 10.1,3.2 11.4,4.1 12,5.1 12.6,4.1 13.9,3.2 15.7,3.2 18.6,3.2 21,5.5 21,8.6 21,14.5 12,21 12,21 Z");
        _likeGlyph = new System.Windows.Shapes.Path
        {
            Data = heart,
            Stroke = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5,
            Fill = Brushes.Transparent,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        var border = new Border
        {
            Child = _likeGlyph,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        border.MouseEnter += (_, _) => border.Background = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            long t0 = Environment.TickCount64;

            // 先给反馈：立刻翻转颜色（不等网易云回执），体感就是"点了就变"
            if (_heartKnown)
            {
                PaintHeart(!_heartLiked);
                Log.Info($"Island: heart painted in {Environment.TickCount64 - t0}ms (optimistic)");
            }
            else
            {
                PaintHeart(true);
            }

            // 后台发键 + 回读校正（不阻塞 UI）
            await _bridge.ToggleLikeAsync();

            await Task.Delay(350);
            var actual = await _bridge.GetLikeStateAsync();
            if (actual is null) { Log.Info("Island: heart read-back unavailable"); return; }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                bool differs = _heartKnown && actual.Value != _heartLiked;
                PaintHeart(actual);
                Log.Info($"Island: heart confirmed {(actual.Value ? "liked" : "not liked")} after {Environment.TickCount64 - t0}ms" + (differs ? " (corrected)" : ""));
            }));
        };

        return border;
    }

    UIElement GlyphButton(string geometry, Action action)
    {
        var text = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(geometry),
            Fill = Frozen(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        var border = new Border
        {
            Child = text,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            action(); e.Handled = true;
        };
        border.MouseEnter += (_, _) => border.Background = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    void ApplyTheme()
    {
        // 固定深色玻璃，和菜单栏同材质
        _card.Background = Frozen(Color.FromArgb(0xF0, 0x1E, 0x20, 0x26));
        _card.BorderBrush = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        _title.Foreground = Frozen(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5));
        _artist.Foreground = Frozen(Color.FromArgb(0xB0, 0xE8, 0xE8, 0xEE));
        _lyric.Foreground = Frozen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
        _elapsed.Foreground = _artist.Foreground;
        _total.Foreground = _artist.Foreground;
        _empty.Foreground = _artist.Foreground;
        _playGlyph.Fill = _title.Foreground;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>在菜单栏正中下方展开（owner 为菜单栏窗口句柄）。</summary>
    public static void ShowCentered(IntPtr owner, MediaWatcher media)
    {
        try
        {
            var island = _open;
            if (island is null)
            {
                island = new IslandWindow { _ownerHandle = owner };
                island.Show();
                _open = island;
            }
            island._media = media;
            island.ApplyTheme();
            island.PositionUnderMenuBar(owner);
            island.Refresh();
            island._ticker.Start();
        island._heartNextProbe = DateTime.MinValue;   // 打开时强制读一次真实收藏状态
        island.RefreshHeartState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Showing the island");
        }
    }
    public static void HideIsland()
    {
        var island = _open;
        _open = null;
        if (island is null) return;
        island._ticker.Stop();          // 收起即停，零开销
        island._media?.PersistPosition();   // 收起时落一次盘
        island._leaveTimer.Stop();
        island.Close();
    }

    void PositionUnderMenuBar(IntPtr owner)
    {
        try
        {
            var display = Displays.ByNameOrPrimary(null);
            double scale = display.Scale <= 0 ? 1 : display.Scale;
            var screen = display.Bounds;
            Width = 316;
            Height = 72;   // 52 封面 + 上下 8 内边距 + 边框：跟内容贴合，底部不留白
            Left = (screen.Width / scale - Width) / 2 + screen.Left / scale;
            Top = screen.Top / scale + 48 / scale;   // 菜单栏高 48px
            // 注意：不调用 Activate() —— 不能把焦点从游戏/应用手里抢走
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Positioning the island");
        }
    }

    /// <summary>按媒体状态刷新卡片（事件驱动调用）。</summary>
    public void Refresh()
    {
        var media = _media;
        bool has = media is not null && media.HasSession && !string.IsNullOrWhiteSpace(media.Title);
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _mediaGrid.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        _title.Text = media!.Title;
        _artist.Text = media.Artist;
        _art.Source = media.Art;
        bool hasArt = media.Art is not null;
        _art.Visibility = hasArt ? Visibility.Visible : Visibility.Hidden;
        if (_artPlaceholder is not null) _artPlaceholder.Visibility = hasArt ? Visibility.Hidden : Visibility.Visible;
        bool playing = media.IsPlaying;   // 以回读到的真实状态为准（命令后 120/450ms 各回读一次）
        SetButtonEnabled(_buttons, media.CanPlayPause || media.CanNext || media.CanPrevious);
        _playGlyph.Opacity = media.CanPlayPause ? 1.0 : 0.35;
        _playGlyph.Data = Geometry.Parse(playing ? PauseGeometry : PlayGeometry);
        UpdateProgressAndLyric();
        StartLyricsFetch(media.Title, media.Artist);
    }

    void StartLyricsFetch(string title, string artist)
    {
        string key = title + "|" + artist;
        if (_lyricsKey == key) return;
        _lyricsKey = key;
        _lyrics = null;
        _lyric.Text = "";
        if (_fetchingLyrics) return;
        _fetchingLyrics = true;
        _ = Task.Run(async () =>
        {
            var info = await LyricsService.GetAsync(title, artist);
            _fetchingLyrics = false;
            if (info is null) { Log.Info($"Lyrics: fetch returned null for '{title}'"); return; }
            Log.Info($"Lyrics: fetched {info.Lyrics?.Lines.Count ?? 0} lines, duration={info.Duration.TotalSeconds:F0}s for '{title}'");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_lyricsKey != key) return;   // 期间又换歌了
                _lyrics = info.Lyrics;
                _media?.SetKnownDuration(info.Duration);   // 网易云 SMTC 不报时长，用元数据里的准确值
                UpdateProgressAndLyric();
            }));
        });
    }

    void UpdateProgressAndLyric()
    {
        // 换歌就立刻重新读收藏状态（新歌是不是已经红心过）
        var nowPlaying = _media;
        if (nowPlaying is not null)
        {
            string key = nowPlaying.Title + "|" + nowPlaying.Artist;
            if (key != _heartSongKey)
            {
                _heartSongKey = key;
                _heartKnown = false;
                PaintHeart(null);
                _heartNextProbe = DateTime.MinValue;
            }
        }

        RefreshHeartState();
        var media = _media;
        if (media is null || !media.HasSession) return;

        media.RefreshTimeline();   // 读一次缓存的时间轴：应用若哪天开始报真实位置，这里就会自动用上
        var position = media.CurrentPosition;
        var duration = media.Duration;
        if (duration > TimeSpan.Zero)
        {
            double ratio = Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            _progressFill.Width = Math.Max(0, _progressTrack.ActualWidth > 0 ? _progressTrack.ActualWidth * ratio : 0);
            _total.Text = Format(duration);
        }
        else
        {
            _progressFill.Width = 0;
            _total.Text = "--:--";
        }
        _elapsed.Text = media.PositionKnown ? Format(position) : "--:--";

        // 歌词行：位置 + 用户偏移（网易云不报位置，残留误差靠配置里的 LyricOffsetMs 修）
        var lyricPosition = position + TimeSpan.FromMilliseconds(_lyricOffsetMs);
        // 位置未知时至少显示第一句，而不是整行空白
        _lyric.Text = _lyrics?.LineAt(media.PositionKnown
            ? (lyricPosition < TimeSpan.Zero ? TimeSpan.Zero : lyricPosition)
            : TimeSpan.Zero) ?? "";

        if (_stateLabel is not null && _stateLabel.Text != Label(media)) Log.Info($"Island: state {media.PlaybackState} known={media.PositionKnown} pos={position.TotalSeconds:F1}s");
        if (_stateLabel is not null)
        {
            _stateLabel.Text = Label(media);
        }

        // 每 5 秒把位置落盘：MacDock 自己重启后能接着算，而不是从 0 开始
        if (Environment.TickCount64 - _lastPersistAt > 5000)
        {
            _lastPersistAt = Environment.TickCount64;
            media.PersistPosition();
        }
    }

    static string Label(MacDock.Core.MediaWatcher media) => media.PlaybackState switch
    {
        MacDock.Core.MediaWatcher.State.Buffering => "缓冲中…",
        MacDock.Core.MediaWatcher.State.Paused => "已暂停",
        MacDock.Core.MediaWatcher.State.Stopped => "已停止",
        _ => "",
    };

    static string Format(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{value.Minutes}:{value.Seconds:00}";
}
