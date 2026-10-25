using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// macOS 的启动台：当前显示器的工作区整块盖上玻璃，开始菜单里的应用铺成 7 × 4 的图标栅格，
/// 顶部是搜索框（认得拼音首字母：「wx」找得到「微信」），底部页码点，方向键 / 滚轮翻页，
/// 回车启动，Esc 或点空白处关闭。
///
/// 和 Dock 的玻璃条一样，内容画在一个透明的分层窗口里，真正带 DWM 亚克的玻璃在它下面单独一个窗口
/// （<see cref="LaunchpadGlass"/>）：分层窗口用不了系统亚克力，非分层窗口又画不了半透明的图标和文字。
/// </summary>
internal sealed class LaunchpadWindow : Window
{
    // 尺寸都是 DIU：150% 缩放下 1 DIU = 1.5 物理像素，所以 84 DIU 的图标 = 126 物理像素
    // macOS 的启动台在 1707 DIU 宽的屏上大约是 7 列 × 4 行铺满 80% 宽：格子 200、图标 120。
    const double CellWidth = 200;
    const double CellHeight = 190;
    const double IconSize = 120;
    const double SearchWidth = 380;
    const double SearchHeight = 38;

    // 只做淡入淡出：缩放整屏可视树会每帧重新栅格化（2560×1600），这是之前"不丝滑"的根因
    const int EntranceMs = 0;     // 0 = 不做动画，直接出现（用户要求"不要动画"）
    const int ExitMs = 0;         // 0 = 直接消失


    static readonly FontFamily Font = Typo.Ui;
    static readonly FontFamily IconFont = Typo.Icons;

    static readonly Brush DarkText = Frozen(0xF5, 0xF5, 0xF7);
    static readonly Brush LightText = Frozen(0x1D, 0x1D, 0x1F);
    static readonly Brush DarkSubtle = Frozen(0xAD, 0xAD, 0xB4);
    static readonly Brush LightSubtle = Frozen(0x5C, 0x5C, 0x60);
    static readonly Brush DarkHover = Frozen(0xFF, 0xFF, 0xFF, 0x0F);
    static readonly Brush LightHover = Frozen(0x00, 0x00, 0x00, 0x16);
    static readonly Brush DarkSelected = Frozen(0xFF, 0xFF, 0xFF, 0x1C);
    static readonly Brush LightSelected = Frozen(0x00, 0x00, 0x00, 0x24);
    static readonly Brush DarkOutline = Frozen(0xFF, 0xFF, 0xFF, 0x24);
    static readonly Brush LightOutline = Frozen(0x00, 0x00, 0x00, 0x1F);
    static readonly Brush DarkBubble = Frozen(0x0A, 0x0A, 0x0E, 0xCC);
    static readonly Brush LightBubble = Frozen(0xFF, 0xFF, 0xFF, 0xE6);
    static readonly Brush DarkDotOn = Frozen(0xFF, 0xFF, 0xFF, 0xE6);
    static readonly Brush DarkDotOff = Frozen(0xFF, 0xFF, 0xFF, 0x4D);
    static readonly Brush LightDotOn = Frozen(0x00, 0x00, 0x00, 0xC8);
    static readonly Brush LightDotOff = Frozen(0x00, 0x00, 0x00, 0x61);
    static readonly Brush DarkField = Frozen(0xFF, 0xFF, 0xFF, 0x1F);
    static readonly Brush DarkFieldBorder = Frozen(0xFF, 0xFF, 0xFF, 0x38);
    static readonly Brush LightField = Frozen(0xFF, 0xFF, 0xFF, 0xB8);   // 浅色下：白 72%，压在亮底上也能看清
    static readonly Brush LightFieldBorder = Frozen(0x00, 0x00, 0x00, 0x1F);

    static LaunchpadWindow? _open;

    /// <summary>True while the launchpad covers the screen: the dock hides itself, as on macOS.</summary>
    public static event Action<bool>? OpenChanged;

    readonly LaunchpadGlass _glass = new();
    readonly Display _display;
    readonly double _fit;                       // 小屏幕 / 高缩放下把整组内容等比缩小
    readonly Grid _root = new();
    readonly Grid _grid = new();                // 7 × 4 图标栅格
    readonly StackPanel _dots = new();
    readonly TextBox _search;
    readonly Border _searchBox;
    readonly TextBlock _placeholder;
    readonly TextBlock _message;
    readonly ScaleTransform _entrance = new(1, 1);   // 保持 1：不做整屏缩放动画
    readonly CancellationTokenSource _cancel = new();
    readonly List<TileView> _tiles = [];
    readonly Brush _text, _subtle, _tileHover, _tileSelected, _tileOutline, _bubble, _bubbleText, _dotOn, _dotOff;

    List<LaunchpadEntry> _all = [];
    List<LaunchpadEntry> _results = [];
    int _selected;      // 在 _results 里的序号
    int _page;          // 当前页
    int _hoverSlot = -1;
    int _wheel;
    int _dotCount = -1;
    IntPtr _hwnd;
    bool _scanning = true;
    bool _shown;
    DateTime _openedAt;   // 点空白关闭的宽限期起点（见 backdrop 的点击处理）
    bool _closing;

    LaunchpadWindow()
    {
        // 配色跟系统主题走（macOS 的启动台也是）：深色=暗玻璃+白字，浅色=白玻璃+深字。
        // 之前按壁纸亮度翻配色，但玻璃遮罩仍按主题算，结果深色下出现"深字压深底"。
        bool dark = Theme.IsDark && !Environment.GetCommandLineArgs().Contains("--light");
        _text = dark ? DarkText : LightText;
        _subtle = dark ? DarkSubtle : LightSubtle;
        _tileHover = dark ? DarkHover : LightHover;
        _tileSelected = dark ? DarkSelected : LightSelected;
        _tileOutline = dark ? DarkOutline : LightOutline;
        _bubble = dark ? DarkBubble : LightBubble;
        _bubbleText = dark ? DarkText : LightText;
        _dotOn = dark ? DarkDotOn : LightDotOn;
        _dotOff = dark ? DarkDotOff : LightDotOff;

        // 启动台开在指针所在的那块屏上（mac 也是如此），没有指针就开在主屏。
        _display = GetCursorPos(out var cursor) ? Displays.FromPoint(cursor) : Displays.Primary();
        double workWidth = _display.Work.Width / _display.Scale, workHeight = _display.Work.Height / _display.Scale;
        _fit = Math.Min(1, Math.Min(
            (workWidth - 96) / (LaunchpadModel.Columns * CellWidth),
            (workHeight - 220) / (LaunchpadModel.Rows * CellHeight)));

        Typo.Crisp(this);
        Title = "MacDock Launchpad";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;                 // 半透明的图标、文字、搜索框
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;                             // 先躲在屏幕外，定位在 SourceInitialized 里做
        Top = -32000;
        Width = Math.Max(1, workWidth);
        Height = Math.Max(1, workHeight);

        _search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = _text,
            CaretBrush = _text,
            SelectionBrush = _tileSelected,
            FontFamily = Font,
            FontSize = 14,
            Padding = new Thickness(0),
            MaxLength = 40,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        TextOptions.SetTextFormattingMode(_search, TextFormattingMode.Display);
        _search.TextChanged += (_, _) => OnSearchChanged();

        _placeholder = new TextBlock
        {
            Text = Loc.T("Search"),
            Foreground = _subtle,
            FontFamily = Font,
            FontSize = 14,
            Margin = new Thickness(3, 0, 0, 0), // 让开光标，否则插在第一笔上
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var glyph = new TextBlock
        {
            Text = "\uE721", // Segoe Fluent Icons: 放大镜
            FontFamily = IconFont,
            FontSize = 12,
            Foreground = _subtle,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(_search, 1);
        Grid.SetColumn(_placeholder, 1);
        field.Children.Add(glyph);
        field.Children.Add(_placeholder);
        field.Children.Add(_search); // 放在占位文字上面，才点得到、输得进

        _searchBox = new Border
        {
            Width = SearchWidth,
            Height = SearchHeight,
            CornerRadius = new CornerRadius(SearchHeight / 2),
            Background = dark ? DarkField : LightField,
            BorderBrush = dark ? DarkFieldBorder : LightFieldBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(13, 0, 13, 0),
            Child = field,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 36, 0, 22),
        };

        _message = new TextBlock
        {
            Text = Loc.T("Loading…"),
            Foreground = _subtle,
            FontFamily = Font,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _dots.Orientation = Orientation.Horizontal;
        _dots.HorizontalAlignment = HorizontalAlignment.Center;
        _dots.Margin = new Thickness(0, 14, 0, 40);

        for (int i = 0; i < LaunchpadModel.Columns; i++) _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellWidth) });
        for (int i = 0; i < LaunchpadModel.Rows; i++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellHeight) });
        _grid.HorizontalAlignment = HorizontalAlignment.Center;
        _grid.VerticalAlignment = VerticalAlignment.Center;
        _grid.ClipToBounds = false;   // 名字第二行要能画出来，不能被格子裁掉

        _root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _root.Opacity = 0;
        // 铺满的空白底板：点它就是"点空白处"，格子与搜索框在它上面，点击根本到不了这里。
        var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        backdrop.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            // 从 Dock 图标点开时，这次点击的落点会落到刚出现的启动台上：宽限期内不当作"点空白"。
            if ((DateTime.UtcNow - _openedAt).TotalMilliseconds < 400) return;
            Close();
        };
        Grid.SetRowSpan(backdrop, 3);
        _root.Children.Add(backdrop);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // 搜索框
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 图标栅格
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                     // 页码点
        Grid.SetRow(_searchBox, 0);
        Grid.SetRow(_grid, 1);
        Grid.SetRow(_message, 1);
        Grid.SetRow(_dots, 2);
        _root.Children.Add(_searchBox);
        _root.Children.Add(_grid);
        _root.Children.Add(_message);
        _root.Children.Add(_dots);
        // 缩放围绕中心：进入动画 0.9 → 1.0，小屏幕的等比缩小也在这里
        // Win+D / 显示桌面 会把启动台也最小化：macOS 这种情况下就是直接关掉。
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Close();
        };

        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = new TransformGroup { Children = { new ScaleTransform(_fit, _fit), _entrance } };
        Content = _root;

        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Start();
    }

    /// <summary>打开启动台（Dock 以后的入口）：同时只许有一个。</summary>
    public static LaunchpadWindow Open()
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return existing;
        }
        var window = new LaunchpadWindow();
        _open = window;
        OpenChanged?.Invoke(true);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, window)) _open = null;
            OpenChanged?.Invoke(false);
        };
        return window;
    }

    /// <summary>
    /// <c>--launchpad</c> 的入口：只开启动台，不建 Dock、不动任务栏；窗口关掉进程就退出。
    /// 放在这个文件里，是为了让 Program.cs 里只多一条命令行分支。
    /// </summary>
    public static int RunStandalone()
    {
        using var mutex = new Mutex(true, @"Local\MacDock.Launchpad", out bool first);
        if (!first) return 0;
        return new LaunchpadApp().Run();
    }

    // ───────────────────────── 窗口与定位 ─────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // 只设 TOOLWINDOW（不进 Alt+Tab / 任务栏），不要 NOACTIVATE：启动台要拿键盘。
        long ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
        // 挂在玻璃窗口下面：玻璃永远在内容之下，两个窗口一起进任务栏的同一个 z 序位置。
        new WindowInteropHelper(this).Owner = _glass.Handle;
        Place();
    }

    /// <summary>铺满当前显示器的工作区，尺寸用物理像素给，免得跟着启动时的 DPI 走偏。</summary>
    /// <summary>整块屏，只让开顶部那条菜单栏（Dock 那条带也要盖住）。</summary>
    RECT ScreenRect()
    {
        var bounds = _display.Bounds;
        int menuBar = (int)Math.Round(26 * _display.Scale);
        return new RECT
        {
            Left = bounds.Left,
            Top = bounds.Top + menuBar,
            Right = bounds.Right,
            Bottom = bounds.Bottom,
        };
    }

    void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        // 铺满整块屏，只给顶部菜单栏留出那条 26 DIU：Dock 那条带也要盖住（Dock 此时已隐藏），
        // 否则启动台底下会露出一条没模糊的桌面。
        var rect = ScreenRect();
        SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _shown = true;
        AnimateIn();
        FocusSearch();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancel.Cancel();
        _cancel.Dispose();
        _tiles.Clear();
        _grid.Children.Clear();
        _glass.Close(); // 全屏玻璃必须一起收走，否则桌面上留一块挡住的玻璃
        base.OnClosed(e);
    }

    /// <summary>Esc、启动、点空白处都走这里：先播完退场动画再真的关。</summary>
    public new void Close()
    {
        if (_closing) return;
        _closing = true;
        _cancel.Cancel();
        if (!_shown)
        {
            CloseNow();
            return;
        }
        AnimateOut();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true; // Alt+F4 也走淡出，不要一下子把全屏窗口抽走
            Close();
            return;
        }
        base.OnClosing(e);
    }

    void CloseNow() => base.Close();

    static readonly CubicEase SmoothIn = new() { EasingMode = EasingMode.EaseOut };
    static readonly CubicEase SmoothOut = new() { EasingMode = EasingMode.EaseIn };

    void AnimateIn()
    {
        if (EntranceMs <= 0)
        {
            // 不做动画：直接把两层置为不透明（避免"先半透明再跳一下"的闪动）
            _root.BeginAnimation(OpacityProperty, null);
            _root.Opacity = 1;
            _glass.SetOpacity(1);
            return;
        }
        _root.Opacity = 0;
        _root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(EntranceMs)) { EasingFunction = SmoothIn });
        _glass.Fade(0, 1, EntranceMs, SmoothIn);
    }

    void AnimateOut()
    {
        if (ExitMs <= 0)
        {
            CloseNow();
            return;
        }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ExitMs)) { EasingFunction = SmoothOut };
        fade.Completed += (_, _) => CloseNow();
        _root.BeginAnimation(OpacityProperty, fade);
        _glass.Fade(1, 0, ExitMs, SmoothOut);
    }

    void FocusSearch()
    {
        Activate();
        _search.Focus();
        Keyboard.Focus(_search);
    }

    // ───────────────────────── 扫描与加载 ─────────────────────────

    /// <summary>开始菜单只在后台线程读：一百多条快捷方式加上图标，放 UI 线程会白屏好几秒。</summary>
    async void Start()
    {
        var token = _cancel.Token;
        try
        {
            _all = await Task.Run(LaunchpadModel.Scan, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the Start menu for the Launchpad");
        }
        if (token.IsCancellationRequested) return;

        _scanning = false;
        ApplyFilter();
        ShowLaunchpad();
        LoadIcons(token);
    }

    /// <summary>玻璃先亮起来（DWM 亚克力在底下），内容窗口紧跟着带缩放进场。</summary>
    void ShowLaunchpad()
    {
        _openedAt = DateTime.UtcNow;
        // 先让 Dock 收起来，抓屏里才不会有 Dock 自己的影子。
        OpenChanged?.Invoke(true);

        try
        {
            var rect = ScreenRect();
            // 先把窗口显示出来：没有任何窗口时 dispatcher 会闲下来、应用直接退出（CLI 启动就是这样）。
            _glass.Show();
            _glass.SetBounds(rect);
            _glass.FadeIn(EntranceMs);
            Show();
            // 窗口已经在屏上了，这里可以安全地晚一拍抓屏（也能避开 Dock 收起的过渡帧）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { _glass.SetBackdrop(ScreenFrost.Capture(rect, divisor: 4, blurRadius: 18)); }
                catch (Exception ex) { Log.Error(ex, "Preparing the launchpad backdrop"); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Showing the Launchpad");
            Close();
        }
    }

    async void LoadIcons(CancellationToken token)
    {
        var entries = _all;
        try
        {
            await Task.Run(() => LaunchpadModel.LoadIcons(entries, IconReady, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Loading the Launchpad's icons");
        }
    }

    /// <summary>后台线程解出一个图标：只刷新它自己的格子，不重建整页。</summary>
    void IconReady(LaunchpadEntry entry)
    {
        if (_cancel.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var tile in _tiles)
                if (ReferenceEquals(tile.Entry, entry)) tile.SetIcon(entry.Icon);
        });
    }

    // ───────────────────────── 过滤、分页与高亮 ─────────────────────────

    int PageCount => (_results.Count + LaunchpadModel.PageSize - 1) / LaunchpadModel.PageSize;

    /// <summary>当前页里的第几格。</summary>
    int SelectedSlot => _selected - _page * LaunchpadModel.PageSize;

    void OnSearchChanged()
    {
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_scanning) return;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var query = _search.Text.Trim();
        _results = query.Length == 0 ? _all : _all.Where(entry => entry.Matches(query)).ToList();
        _selected = -1;   // 不预选：打开时不该有一个格子自己亮着
        ShowPage(0);
    }

    void ShowPage(int index)
    {
        _page = PageCount == 0 ? 0 : Math.Clamp(index, 0, PageCount - 1);
        _hoverSlot = -1;
        _grid.Children.Clear();
        _tiles.Clear();

        int start = _page * LaunchpadModel.PageSize;
        for (int slot = 0; slot < LaunchpadModel.PageSize && start + slot < _results.Count; slot++)
        {
            var tile = new TileView(this, _results[start + slot]);
            Grid.SetColumn(tile.Root, slot % LaunchpadModel.Columns);
            Grid.SetRow(tile.Root, slot / LaunchpadModel.Columns);
            _grid.Children.Add(tile.Root);
            _tiles.Add(tile);
        }

        _message.Text = _scanning ? Loc.T("Loading…") : Loc.T("No Results");
        _message.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildDots();
        UpdateSelection();
    }

    void BuildDots()
    {
        int pages = PageCount;
        if (pages != _dotCount)
        {
            _dotCount = pages;
            _dots.Children.Clear();
            for (int i = 0; i < pages; i++)
            {
                var dot = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Margin = new Thickness(4, 0, 4, 0),
                    Fill = _dotOff,
                    Tag = i,
                };
                dot.MouseLeftButtonUp += (sender, args) =>
                {
                    args.Handled = true;
                    Select((int)((Ellipse)sender).Tag * LaunchpadModel.PageSize);
                };
                _dots.Children.Add(dot);
            }
        }
        for (int i = 0; i < _dots.Children.Count; i++)
            if (_dots.Children[i] is Ellipse dot) dot.Fill = i == _page ? _dotOn : _dotOff;
        _dots.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed; // 只有一页时 mac 也不画点
    }

    void UpdateSelection()
    {
        for (int slot = 0; slot < _tiles.Count; slot++) _tiles[slot].Apply(slot == SelectedSlot, slot == _hoverSlot);
    }

    /// <summary>选中第 index 项（全量结果里的序号），需要时换页。</summary>
    void Select(int index)
    {
        if (_results.Count == 0) return;
        index = Math.Clamp(index, 0, _results.Count - 1);
        _selected = index;
        int page = index / LaunchpadModel.PageSize;
        if (page != _page) ShowPage(page);
        else UpdateSelection();
    }

    void Move(int delta) => Select(_selected < 0 ? 0 : _selected + delta);   // 未选中时任何方向键都落在第一格

    void TurnPage(int delta)
    {
        if (PageCount <= 1) return;
        int page = Math.Clamp(_page + delta, 0, PageCount - 1);
        if (_selected < 0)
        {
            // 滚轮/PageUp 只翻页，不建立选中：否则每滑一下就点亮第一格。
            ShowPage(page);
            return;
        }
        // 已经有选中项时，把它带到新页的第一格，方向键接着就能用。
        Select(page * LaunchpadModel.PageSize);
    }

    void SetHover(int slot)
    {
        if (_hoverSlot == slot) return;
        _hoverSlot = slot;
        UpdateSelection();
    }

    void Launch(LaunchpadEntry entry)
    {
        if (_closing) return;
        LaunchpadModel.Launch(entry);
        Close(); // 启动后自动收起，和 mac 一样
    }

    void ShowMenu(LaunchpadEntry entry)
    {
        // 统一的 macOS 风格弹窗（原来是 WPF ContextMenu，白底直角，和启动台完全不搭）
        var menu = new PopMenu();
        menu.Add(Loc.T("Open"), () => Launch(entry));
        menu.Add(Loc.T("Show in File Explorer"), () => LaunchpadModel.Reveal(entry));
        _menu = menu;
        menu.Closed += () => { _menu = null; FocusSearch(); };   // 菜单关掉后键盘回到搜索框
        if (GetCursorPos(out var p)) menu.Show(p.X, p.Y);
    }

    PopMenu? _menu;

    // ───────────────────────── 输入 ─────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                Move(-1);
                break;
            case Key.Right:
                Move(1);
                break;
            case Key.Up:
                Move(-LaunchpadModel.Columns);
                break;
            case Key.Down:
                Move(LaunchpadModel.Columns);
                break;
            case Key.PageUp:
                TurnPage(-1);
                break;
            case Key.PageDown:
                TurnPage(1);
                break;
            case Key.Enter:
                if (_results.Count > 0 && _selected >= 0) Launch(_results[_selected]);
                break;
            default:
                return; // 其余按键（包括退格）留给搜索框
        }
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (e.Delta == 0) return;
        _wheel += e.Delta;
        // 触控板一次滚一格只走 10~30：攒够一格滚轮（120）再翻页，否则会连着翻好几页
        if (Math.Abs(_wheel) >= 120)
        {
            TurnPage(_wheel < 0 ? 1 : -1);
            _wheel = 0;
        }
        e.Handled = true;
    }


    static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ───────────────────────── 一格图标 ─────────────────────────

    /// <summary>一个应用：图标、名称、悬停/选中高亮，以及左键启动、右键菜单。</summary>
    sealed class TileView
    {
        readonly LaunchpadWindow _owner;
        readonly Image _icon;
        readonly Border _bubble;
        readonly TextBlock _label;

        public TileView(LaunchpadWindow owner, LaunchpadEntry entry)
        {
            _owner = owner;
            Entry = entry;

            _icon = new Image
            {
                Source = entry.Icon,
                Width = IconSize,
                Height = IconSize,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

            _label = new TextBlock
            {
                Text = entry.Name,
                Foreground = owner._text,
                FontFamily = Font,
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = CellWidth - 24,
                MaxHeight = 48, // 两行 13 DIU 的正文，留出下伸部（不然 "Tools" 会被切掉半截）
            };
            TextOptions.SetTextFormattingMode(_label, TextFormattingMode.Display);
            // 不再给每个标签加 DropShadowEffect：28 个/页 × 软件光栅化，翻页时最贵的一笔。

            _bubble = new Border
            {
                Child = _label,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(0, 7, 0, 0),
                Background = Brushes.Transparent,
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(_icon);
            stack.Children.Add(_bubble);

            Root = new Border
            {
                Child = stack,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(6),
                Margin = new Thickness(3),
                BorderThickness = new Thickness(1), // 常驻 1px：选中时不会因为加边框把图标挤动
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent,
                };
            Root.MouseEnter += (_, _) => owner.SetHover(Slot);
            Root.MouseLeave += (_, _) => owner.SetHover(-1);
            Root.MouseLeftButtonDown += (_, args) => args.Handled = true;
            Root.MouseLeftButtonUp += (_, args) =>
            {
                args.Handled = true;
                owner.Select(owner._page * LaunchpadModel.PageSize + Slot);
                owner.Launch(entry);
            };
            Root.MouseRightButtonUp += (_, args) =>
            {
                args.Handled = true;
                owner.Select(owner._page * LaunchpadModel.PageSize + Slot);
                owner.ShowMenu(entry);
            };
        }

        public LaunchpadEntry Entry { get; }

        public Border Root { get; }

        int Slot => (int)(Grid.GetColumn(Root) + Grid.GetRow(Root) * LaunchpadModel.Columns);

        public void SetIcon(ImageSource? icon)
        {
            if (icon is not null) _icon.Source = icon;
        }

        /// <summary>
        /// 只画一层极淡的底（选中比悬停稍亮）+ 极细描边，名字始终是纯文字 —— macOS 的启动台没有
        /// "名字药丸"，那玩意儿压在图标下面既挡邻居又显脏。
        /// </summary>
        public void Apply(bool selected, bool hovered)
        {
            Root.Background = selected ? _owner._tileSelected : hovered ? _owner._tileHover : Brushes.Transparent;
            Root.BorderBrush = selected ? _owner._tileOutline : Brushes.Transparent;
            _bubble.Background = Brushes.Transparent;
            _label.Foreground = _owner._text;
        }
    }
}

/// <summary>
/// 启动台底下那块玻璃：整块工作区的 DWM 亚克力（透出来的桌面是糊的，正好压住背景不抢眼），
/// 上面盖一层半透明深色，白字才读得清。淡入淡出靠动画这层颜色的透明度——分层窗口才能改整体不透明度，
/// 这块是非分层窗口（为了系统亚克力），只能这么淡。
/// </summary>
internal sealed class LaunchpadGlass : AcrylicWindow   // 只用它的分层窗口骨架，不用 DWM 模糊
{
    /// <summary>真透明：不要 DWM 的 mica/acrylic 材料（在这台机器上它是一块不透明灰板）。</summary>
    protected override bool UseSystemBackdrop => false;

    readonly Border _tint;
    readonly Image _veil = new() { Stretch = Stretch.Fill };
    readonly Grid _layers = new() { ClipToBounds = true };

    public LaunchpadGlass() : base(rounded: false)
    {
        Title = "MacDock Launchpad Glass";
        // 打开前抓的一张屏幕 + 模糊一次（macOS 那种 vibrancy）：背后是真实桌面，零持续开销。
        _tint = new Border();
        _layers.Children.Add(_veil);
        _layers.Children.Add(_tint);
        Content = _layers;
    }

    /// <summary>设置"打开前那一帧屏幕"的模糊版；null 表示没抓到，退回纯半透明。</summary>
    public void SetBackdrop(ImageSource? blurred)
    {
        _veil.Source = blurred;
        _veil.Visibility = blurred is null ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Same wallpaper frost as the menu bar: the OS blur on this build is nearly opaque, and a
        // launchpad that hides the desktop entirely is exactly what macOS does not do.
        // 不用系统的 accent 模糊：这台 Win11 上它渲染成一块不透明的灰板（就是"浅色启动台很丑"的根因）。
        // 启动台本身就是分层窗口，靠 tint 的真透明即可。
    }

    public override void ApplyTheme()
    {
        ApplyGlassTheme(DWMWA_COLOR_NONE);
        var brush = new SolidColorBrush(Tint(1));
        brush.Freeze();
        _tint.Background = brush;
    }

    public void FadeIn(int ms) => Fade(0, 1, ms);

    /// <summary>Fades the whole glass, wallpaper layer included: cross-fading only the tint would flash.</summary>
    public void Fade(double from, double to, int ms) => Fade(from, to, ms, new QuadraticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>带缓动的淡入淡出（内容窗口用同一组参数，两层完全同步）。</summary>
    /// <summary>不做动画，直接把不透明度设成某个值。</summary>
    public void SetOpacity(double value)
    {
        _layers.BeginAnimation(OpacityProperty, null);
        _layers.Opacity = value;
    }

    public void Fade(double from, double to, int ms, System.Windows.Media.Animation.IEasingFunction ease)
    {
        _layers.BeginAnimation(OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
        });
    }

    /// <summary>How dark the veil is: macOS keeps the desktop readable through Launchpad, so this is a
    /// thin tint over the blur rather than the near-black sheet it used to be.</summary>
    static Color Tint(double alpha) => Theme.IsDark
        ? Color.FromArgb((byte)Math.Round(alpha * 0xF0), 0x14, 0x14, 0x18)   // 94%：实底，白字清晰
        : Color.FromArgb((byte)Math.Round(alpha * 0xF0), 0xF4, 0xF6, 0xFA);   // 94%：实底（真透明在全屏窗口上这台机器给不了，见笔记）
}

/// <summary>
/// <c>--launchpad</c> 的进程外壳：只为一个窗口起一个 Application，窗口关掉进程就退出；
/// 语言跟配置走，启动台里的文字才会和别人做好的中文表一致。
/// </summary>
internal sealed class LaunchpadApp : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception");
            args.Handled = true;
        };
        Theme.Refresh();
        Loc.Init(DockConfig.LoadOrCreate().Settings.Language);
        Log.Info("Launchpad opened from the command line");
        MainWindow = LaunchpadWindow.Open();
    }
}
