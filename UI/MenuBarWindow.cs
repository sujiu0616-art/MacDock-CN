using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MacDock.Core;
using MacDock.Interop;
using Microsoft.Win32;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// The macOS-style menu bar of one monitor: a full-width strip along its top edge, registered as an
/// app bar so maximized windows start below it. Left: a Start logo and the name of the app in front.
/// Right: third-party tray icons (primary monitor only, as on the Windows 11 taskbar), Wi-Fi /
/// volume / battery (Quick Settings) and the date and time (notifications and calendar). Like the
/// dock, it is a transparent window holding the content over a separate acrylic window
/// (<see cref="MenuBarGlass"/>). What it shows comes from <see cref="MenuBars"/>.
/// </summary>
internal sealed class MenuBarWindow : Window
{
    const double BarHeight = 32; // DIP：盖满任务栏留出的顶部预留（48px @150%），最大化窗口时不再有空隙

    readonly MenuBars _owner;
    readonly string _device;
    readonly MenuBarGlass _glass = new();
    readonly Grid _root = new() { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) }; // alpha 1: clickable
    string? _clockTip;      // 用 NameTip 自己画，别用 WPF ToolTip（会跑到屏幕左上角）
    string? _statusTip;

    readonly Border _logo;
    readonly TextBlock _appName;
    readonly TrayIconsPanel? _tray;
    readonly Border _status;
    readonly TextBlock _wifi, _wifiBack, _volume, _battery;
    readonly Border _clock;
    readonly TextBlock _clockText;

    TextBlock? _inputText;
    Border? _inputCell;
    readonly System.Windows.Threading.DispatcherTimer _inputTimer = new();

    // ── 灵动岛（媒体胶囊）────────────────────────────────────────────────
    Border? _capsule;
    TextBlock? _capsuleText;
    TextBlock? _capsuleIcon;
    System.Windows.Shapes.Ellipse? _capsuleArt;
    Grid? _capsuleViewport;
    TranslateTransform? _capsuleShift;
    MediaWatcher? _media;

    /// <summary>当前活动的媒体中枢（DockWindow 在睡眠唤醒/解锁时会用它重建会话）。</summary>
    public static MediaWatcher? Instance { get; private set; }

    void EnsureMediaWatcher()
    {
        if (_media is not null) return;
        _media = new MediaWatcher(Dispatcher);
        _media.Changed += OnMediaChanged;
        _media.Start();
        _media.StartPersistence();
        Instance = _media;
    }

    void OnMediaChanged()
    {
        UpdateCapsule();
        // 媒体没了（暂停到无会话/切到别的播放器又停了）→ 展开的卡片一起收起来
        if (IslandWindow.IsOpen && (_media is null || !_media.HasSession || string.IsNullOrWhiteSpace(_media.Title)))
            IslandWindow.HideIsland();
        if (IslandWindow.IsOpen) IslandWindow.ShowCentered(_hwnd, _media!);
    }

    void UpdateCapsule()
    {
        if (_capsuleText is null || _media is null) return;
        // 有会话且有元数据就显示（暂停也显示）；只有会话消失/无标题时才收起
        bool playing = _media.HasMetadata;
        // 胶囊里显示 "歌名 - 歌手"，和网易云自己的胶囊一样
        _capsuleText.Text = playing
            ? (string.IsNullOrWhiteSpace(_media.Artist) ? _media.Title : $"{_media.Title} - {_media.Artist}")
            : "";
        if (_capsuleArt?.Fill is ImageBrush brush)
        {
            // 有封面就显示封面；没封面给一个淡色占位圆，保持文字对齐稳定
            bool hasArt = playing && _media.Art is not null;
            _capsuleArt.Width = _capsuleArt.Height = playing ? 18 : 0;
            brush.ImageSource = playing ? _media.Art : null;
            _capsuleArt.Fill = hasArt ? (Brush)brush : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
        if (_capsuleIcon is not null)
            _capsuleIcon.Opacity = playing ? 1.0 : 0.45;
        // 没在播放就**完全不显示**：不留占位、不建窗口、不跑动画（省资源也更好看）
        if (_capsule is not null) _capsule.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        FitCapsule(playing);
    }

    /// <summary>
    /// 胶囊宽度：按文字实际宽度自适应（最多 300px）；超过上限就让它滚动而不是截断。
    /// </summary>
    void FitCapsule(bool playing)
    {
        if (_capsule is null || _capsuleText is null || _capsuleViewport is null) return;

        const double MaxCapsule = 300;
        const double Padding = 22;          // 左右内边距
        double artWidth = _capsuleArt is not null && _capsuleArt.Width > 0 ? _capsuleArt.Width + _capsuleArt.Margin.Right : 0;

        if (!playing)
        {
            StopMarquee();
            _capsuleViewport.Width = 0;
            _capsule.Width = 44;
            return;
        }

        _capsuleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = _capsuleText.DesiredSize.Width;
        double maxViewport = MaxCapsule - artWidth - Padding;
        double viewport = Math.Min(textWidth, maxViewport);

        _capsuleViewport.Width = viewport;
        _capsule.Width = viewport + artWidth + Padding;

        if (textWidth > viewport + 2) StartMarquee(textWidth - viewport);
        else StopMarquee();
    }

    /// <summary>左右来回滚动（28px/s）；只在溢出时运行。</summary>
    void StartMarquee(double overflow)
    {
        if (_capsuleShift is null) return;
        double seconds = Math.Max(1.2, overflow / 28.0);
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            BeginTime = TimeSpan.FromSeconds(0.9),
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
        };
        _capsuleShift.BeginAnimation(TranslateTransform.XProperty, animation, System.Windows.Media.Animation.HandoffBehavior.SnapshotAndReplace);
    }

    void StopMarquee()
    {
        if (_capsuleShift is null) return;
        _capsuleShift.BeginAnimation(TranslateTransform.XProperty, null);
        _capsuleShift.X = 0;
    }

    void UpdateCapsuleLook(bool hover)
    {
        if (_capsule is null) return;
        bool dark = Theme.IsDark;
        byte alpha = (byte)(hover ? 0x3A : 0x22);
        _capsule.Background = new SolidColorBrush(dark
            ? Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(alpha, 0x00, 0x00, 0x00));
        _capsule.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb((byte)(alpha + 0x10), 0xFF, 0xFF, 0xFF)
            : Color.FromArgb((byte)(alpha + 0x10), 0x00, 0x00, 0x00));
        if (_capsuleText is not null)
            _capsuleText.Foreground = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x20, 0x20, 0x24));
    }

    void ToggleIsland()
    {
        EnsureMediaWatcher();
        // 没有正在播放的媒体：不创建岛屿窗口（省资源）
        if (_media is null || !_media.HasSession || string.IsNullOrWhiteSpace(_media.Title)) return;
        if (IslandWindow.IsOpen) { IslandWindow.HideIsland(); return; }
        IslandWindow.ShowCentered(_hwnd, _media!);
    }

    readonly List<TextBlock> _texts = [];
    readonly List<Shape> _shapes = [];

    AppBar? _appBar;
    IntPtr _hwnd;
    uint _taskbarCreatedMessage;
    uint _explorerPid;
    bool _fullscreen;
    bool _startOpenAtPress;
    bool _placePending;
    IntPtr _placedMonitor;

    public MenuBarWindow(MenuBars owner, string device, bool primary)
    {
        _owner = owner;
        _device = device;
        Title = "MacDock Menu Bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = -100;
        Width = 100;
        Height = BarHeight;

        // The glass lives in its own window; keep it right below us in the z-order.
        _glass.Show();
        new WindowInteropHelper(this).Owner = _glass.Handle;

        _logo = Clickable(WindowsLogo(), 9);
        // 左键 = 启动台（我们自己的）。不再拉 Windows 开始菜单：碰它一次，SearchHost /
        // StartMenuExperienceHost / TextInputHost 就会常驻（合计约 430MB）。
        // 需要原生开始菜单：Shift + 左键；右键仍是 Win+X 快捷菜单。
        _logo.MouseLeftButtonUp += (_, _) =>
        {
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) StartMenu.Toggle();
            else LaunchpadWindow.Open();
        };
        _logo.MouseRightButtonUp += (_, _) => StartMenu.ShowQuickLinkMenu();

        _appName = Text("", 13, FontWeights.SemiBold); // macOS 用半粗：13 Bold 在 150% 缩放下会糊成一团
        _appName.Margin = new Thickness(4, 0, 0, 0);
        _appName.VerticalAlignment = VerticalAlignment.Center;

        // One tray service per session: it lives in the primary monitor's bar.
        if (primary) _tray = new TrayIconsPanel { Margin = new Thickness(0, 2, 4, 2) };

        _wifiBack = Glyph("");
        _wifiBack.Opacity = 0.3;
        _wifi = Glyph("");
        var wifiCell = new Grid { Margin = new Thickness(0, 0, 6, 0) };
        wifiCell.Children.Add(_wifiBack);
        wifiCell.Children.Add(_wifi);
        _volume = Glyph("");
        _volume.Margin = new Thickness(0, 0, 6, 0);
        _battery = Glyph("");
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(wifiCell);
        statusRow.Children.Add(_volume);
        statusRow.Children.Add(_battery);
        _status = Clickable(statusRow, 6);
        _status.MouseLeftButtonUp += (_, _) => ShellFlyouts.QuickSettings();
        _status.MouseEnter += (_, _) => NameTip.ShowFor(_status, _statusTip);
        _status.MouseLeave += (_, _) => NameTip.HideTip();

        _clockText = Text("", 13, FontWeights.Normal);
        _clockText.VerticalAlignment = VerticalAlignment.Center;
        _clock = Clickable(_clockText, 8);
        _clock.MouseLeftButtonUp += (_, _) => ShellFlyouts.NotificationCenter();
        _clock.MouseEnter += (_, _) => NameTip.ShowFor(_clock, _clockTip);
        _clock.MouseLeave += (_, _) => NameTip.HideTip();

        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        left.Children.Add(_logo);
        left.Children.Add(_appName);
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
        };
        // 输入法指示器：隐藏了 Windows 任务栏就看不到系统的"中/英"，这里补一格。
        _inputText = Text("中", 12, FontWeights.Normal);
        _inputText.VerticalAlignment = VerticalAlignment.Center;
        _inputCell = Clickable(_inputText, 7);
        InputIndicator.Attach(this, _inputCell);
        right.Children.Add(_inputCell);

        if (_tray is not null) right.Children.Add(_tray);
        right.Children.Add(_status);
        right.Children.Add(_clock);

        // 每 500ms 跟一次前台窗口的输入法状态（读一次 IME 转换模式，几乎零成本）
        _inputTimer.Interval = TimeSpan.FromMilliseconds(500);
        _inputTimer.Tick += (_, _) => UpdateInputLabel();
        _inputTimer.Start();

        // 灵动岛：菜单栏正中的小胶囊（收起态没有任何动画/计时器）
        _capsuleText = new TextBlock
        {
            Text = "",
            FontFamily = Typo.Ui,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,   // 溢出靠滚动，不截断
        };
        _capsule = new Border
        {
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    // 圆形封面：用 Ellipse + ImageBrush（Border + CornerRadius 不会把子元素裁成圆角）
                    (_capsuleArt = new System.Windows.Shapes.Ellipse
                    {
                        Width = 18,
                        Height = 18,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Fill = new ImageBrush { Stretch = Stretch.UniformToFill },
                    }),
                    // 裁剪容器：文字超出可视宽度时在它里面滚动
                    (_capsuleViewport = new Grid
                    {
                        ClipToBounds = true,
                        VerticalAlignment = VerticalAlignment.Center,
                        RenderTransform = (_capsuleShift = new TranslateTransform(0, 0)),
                    }),
                },
            },
            Width = 92,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
        };
        _capsuleViewport!.Children.Add(_capsuleText);
                _capsule.MouseLeftButtonUp += (_, _) => ToggleIsland();
        _capsule.MouseEnter += (_, _) => UpdateCapsuleLook(hover: true);
        _capsule.MouseLeave += (_, _) => UpdateCapsuleLook(hover: false);
        _root.Children.Add(_capsule);
        EnsureMediaWatcher();
        UpdateCapsule();
        UpdateCapsuleLook(hover: false);

        _root.Children.Add(left);
        _root.Children.Add(right);
        Content = _root;
    }

    const int RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_UPDATENOW = 0x0100, RDW_ALLCHILDREN = 0x0080;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, int flags);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        MakeToolWindow(_hwnd);
        // 分层窗口 + DWM 默认圆角 = 左上角被削出的黑缺角：显式关掉圆角偏好。
        int square = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        // 去掉标题栏系统菜单位：否则无边框窗口的左上角会被画成一块 21×21 的黑方块。
        long style = (long)GetWindowLongPtr(_hwnd, GWL_STYLE);
        SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)(style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _explorerPid = TaskbarState.ExplorerProcessId();
        _appBar = new AppBar(_hwnd);
        _appBar.Register();
        // Moved to a monitor with another scale, WPF resizes the window to its own idea of the size.
        DpiChanged += (_, _) => SchedulePlace();

        Place();
        Typo.Crisp(this);
        ApplyTheme();
        ShowAppName(_owner.AppName);
        ShowClock(_owner.ClockText, _owner.ClockTip);
    }

    /// <summary>Gives the reserved strip and the tray back.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _appBar?.Dispose();
        _appBar = null;
        _glass.Close();
        base.OnClosed(e);
    }

    public void ApplyTheme()
    {
        _glass.ApplyTheme();
        ApplyForeground();
        if (_tray is not null) _tray.HoverBrush = HoverBrush;
    }

    /// <summary>
    /// Text and glyphs follow the glass, not the Windows theme: the bar now shows a blurred crop of the
    /// wallpaper, so a bright wallpaper turns it bright and white text would disappear into it.
    /// </summary>
    void ApplyForeground()
    {
        _darkText = Theme.IsDark;
        var text = new SolidColorBrush(_darkText ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        text.Freeze();
        foreach (var t in _texts) t.Foreground = text;
        foreach (var s in _shapes) s.Fill = text;
    }

    /// <summary>After sleep or unlock DWM may show the glass as inactive (flat colour): make it active again.</summary>
    public void Reactivate() => _glass.Reactivate();

    bool _darkText = true;
    Brush HoverBrush => _darkText ? HoverDark : HoverLight;
    static readonly Brush HoverDark = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    static readonly Brush HoverLight = Frozen(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ───────────────────────── Placement ─────────────────────────

    public void SchedulePlace(bool forget = false)
    {
        if (forget) _appBar?.ForgetPosition();
        if (_placePending) return;
        _placePending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _placePending = false;
            Place();
        });
    }

    internal static int DiagPlaces;
    internal static int DiagPosChanged;

    void Place()
    {
        DiagPlaces++;
        if (_hwnd == IntPtr.Zero || _appBar is null) return;
        var display = Displays.All().FirstOrDefault(d => string.Equals(d.DeviceName, _device, StringComparison.OrdinalIgnoreCase));
        if (display.Handle == IntPtr.Zero) return; // unplugged: MenuBars closes this bar

        if (_placedMonitor != IntPtr.Zero && _placedMonitor != display.Handle) _appBar.Reset();
        _placedMonitor = display.Handle;

        int height = (int)Math.Ceiling(BarHeight * display.Scale);
        var bar = _appBar.SetPosition(ABE_TOP, display.Bounds, height, ownEdge: true);   // 自己占住屏幕顶边
        var rect = new RECT(display.Bounds.Left, bar.Top, display.Bounds.Right, bar.Top + height);
        // Moving an app bar notifies the others, which move and notify back: only move when needed.
        if (!GetWindowRect(_hwnd, out var current) || !current.Equals(rect))
            SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
        _glass.SetBounds(rect);
        _root.Width = rect.Width / display.Scale;
        _root.Height = rect.Height / display.Scale;
        _tray?.SetHostRect(rect);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0112 when ((long)wParam & 0xFFF0) == 0xF020:   // WM_SYSCOMMAND / SC_MINIMIZE
                handled = true;
                return IntPtr.Zero;
            case 0x0005 when (int)wParam == 1:                    // WM_SIZE / SIZE_MINIMIZED
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                handled = true;
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                // Clicks never take the focus from the app in front, as on the Mac.
                handled = true;
                return MA_NOACTIVATE;
            case WM_DISPLAYCHANGE:
                SchedulePlace(forget: true);
                return IntPtr.Zero;
            case WM_WINDOWPOSCHANGED:
                _appBar?.NotifyWindowPosChanged();
                return IntPtr.Zero;
        }
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // The tray service broadcasts this too; only a new Explorer has forgotten our app bar.
            uint pid = TaskbarState.ExplorerProcessId();
            if (pid != 0 && pid != _explorerPid)
            {
                _explorerPid = pid;
                _appBar?.Reregister();
                SchedulePlace(forget: true);
            }
        }
        else if (_appBar is not null && msg == _appBar.CallbackMessage)
        {
            switch ((int)wParam)
            {
                case ABN_POSCHANGED:
                    DiagPosChanged++;
                    SchedulePlace();
                    break;
                case ABN_FULLSCREENAPP:
                    // 统一由 Dock 的检测驱动（MenuBars.SetFullscreen）；这里独立处理会导致
                    // "菜单栏藏了、Dock 还在"的割裂画面。
                    break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Step aside while a full-screen app (video, game, presentation) is in front on this monitor.</summary>
    /// <summary>由 Dock 的全屏检测统一驱动（不依赖 Explorer 的 ABN_FULLSCREENAPP）。</summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        if (fullscreen)
        {
            ShowWindow(_hwnd, SW_HIDE);
            _glass.SetVisible(false);
        }
        else
        {
            _glass.SetVisible(true);
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SchedulePlace();
        }
    }

    // ───────────────────────── Content ─────────────────────────

    /// <summary>开机时用：菜单栏淡入。</summary>
    public void FadeIn(int ms)
    {
        _root.Opacity = 0;
        _root.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
    }

    public void ShowAppName(string name)
    {
        if (_appName.Text == name) return;
        _appName.Text = name;
        RepaintSurface();
    }

    /// <summary>
    /// 强制整块分层表面重绘。分层窗口只按脏矩形上传，换字后旧字形会留在没被重传的区域
    /// （看到的效果就是"Firefox"叠在一段灰色旧文字上）。
    /// </summary>
    void RepaintSurface()
    {
        // 只标脏重画，绝不再 Hide/Show 窗口：
        // 浏览器每切一个标签就改一次标题，Hide/Show 会把 DWM 合成搞出黑帧（火狐在前台时最明显）。
        // 之前那个 hide/show 是为清"应用名重影"，真凶已查明是 Firefox 的 Battery Watcher 窗口，
        // 现在由 StripGuard 负责把它移走。
        _root.InvalidateVisual();
    }

    /// <summary>
    /// Shows the date and time. <see cref="MenuBars"/> owns when the clock ticks and words the
    /// tooltip, but the layout of the string on screen belongs to the bar (localized, as on macOS),
    /// so the moment is formatted again here rather than parsed back out of the owner's text.
    /// </summary>
    /// <summary>刷新"中/英"字样（跟随前台窗口的输入法状态）。</summary>
    void UpdateInputLabel()
    {
        if (_inputText is null) return;
        string label = InputIndicator.CurrentLabel();
        if (_inputText.Text != label) _inputText.Text = label;
    }
    public void ShowClock(string text, string tip)
    {
        _clockText.Text = text;
        _clockTip = string.IsNullOrEmpty(tip) ? text : tip;
    }

    public void ShowStatus(SystemStatus s)
    {
        _wifiBack.Visibility = s.Network == NetworkKind.Wifi ? Visibility.Visible : Visibility.Collapsed;
        (_wifi.Text, string wifiTip) = s.Network switch
        {
            NetworkKind.Wifi => (s.WifiSignal switch
            {
                < 25 => "",
                < 50 => "",
                < 75 => "",
                _ => "",
            }, Loc.T("Wi-Fi: signal {0}%", s.WifiSignal)),
            NetworkKind.Ethernet => ("", Loc.T("Ethernet: connected")),
            _ => ("", Loc.T("No Internet connection")),
        };

        int volume = (int)Math.Round((s.Volume ?? 0) * 100);
        _volume.Text = s.Volume is null || s.Muted ? "" : volume switch
        {
            0 => "",
            < 34 => "",
            < 67 => "",
            _ => "",
        };
        string volumeTip = s.Volume is null ? Loc.T("No audio device")
            : s.Muted ? Loc.T("Volume: muted") : Loc.T("Volume: {0}%", volume);

        _battery.Visibility = s.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        int level = (int)Math.Round((s.BatteryPercent ?? 100) / 10.0);
        _battery.Text = ((char)((s.Charging ? 0xEBAB : 0xEBA0) + level)).ToString();
        string batteryTip = s.BatteryPercent is int pct ? (s.Charging ? Loc.T("Battery: {0}% (charging)", pct) : Loc.T("Battery: {0}%", pct)) : Loc.T("Battery");

        var tips = new List<string> { wifiTip, volumeTip };
        if (s.HasBattery) tips.Add(batteryTip);
        _statusTip = string.Join(" · ", tips);
    }

    // ───────────────────────── Building blocks ─────────────────────────

    TextBlock Text(string text, double size, FontWeight weight)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Typo.Ui,
            FontSize = size,
            FontWeight = weight,
        };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
        _texts.Add(t);
        return t;
    }

    TextBlock Glyph(string glyph)
    {
        var t = Text(glyph, 16, FontWeights.Normal); // macOS menu extras are 16 DIU
        t.FontFamily = Typo.Icons;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// <summary>The four panes of the Windows logo, drawn in the text colour like the Apple menu.</summary>
    UIElement WindowsLogo()
    {
        const double pane = 5.5, gap = 1.2;
        var grid = new Grid { Width = pane * 2 + gap, Height = pane * 2 + gap, VerticalAlignment = VerticalAlignment.Center };
        for (int row = 0; row < 2; row++)
        {
            for (int col = 0; col < 2; col++)
            {
                var r = new Rectangle
                {
                    Width = pane,
                    Height = pane,
                    HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    VerticalAlignment = row == 0 ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                };
                _shapes.Add(r);
                grid.Children.Add(r);
            }
        }
        return grid;
    }

    /// <summary>A menu-bar item: rounded highlight under the pointer, like the Mac's menu extras.</summary>
    Border Clickable(UIElement child, double padding)
    {
        var b = new Border
        {
            Child = child,
            Padding = new Thickness(padding, 0, padding, 0),
            Margin = new Thickness(0, 2, 0, 2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
        };
        ToolTipService.SetInitialShowDelay(b, 600);
        b.MouseEnter += (_, _) => b.Background = HoverBrush;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }
}

