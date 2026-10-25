using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 自绘的弹出菜单（任务栏、菜单栏、启动台共用）。
///
/// 为什么不用 WPF 的 ContextMenu：它的模板在这套"分层 + 不激活"的窗口上不绘制，
/// 而默认外观又是 Windows 那套白底直角菜单，跟 macOS 风格的 Dock 完全不搭。
/// 这里自己画：分层窗口 + 圆角玻璃卡片 + 1px 描边 + 圆角高亮，弹出时淡入 90ms。
///
/// 交互与系统菜单一致：点外面关、Esc 关、悬停出子菜单、点条目执行后关闭。
/// </summary>
internal sealed class PopMenu : IDisposable
{
    const double MaxWidth = 340;

    readonly Window _window = new()
    {
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ShowInTaskbar = false,
        ShowActivated = false,
        ResizeMode = ResizeMode.NoResize,
        Topmost = true,
        SizeToContent = SizeToContent.WidthAndHeight,
        Title = "MacDock Menu",
    };

    readonly Border _card;
    readonly StackPanel _rows = new();
    readonly List<(Border Row, PopMenu? Child, bool Leaf)> _entries = [];
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(40) };

    PopMenu? _parent;

    static readonly List<PopMenu> Open = [];

    /// <summary>钩子要用的只读视图。</summary>
    internal static IReadOnlyList<PopMenu> OpenMenus => Open;

    /// <summary>菜单窗口的句柄（用于判断点击是否落在菜单内）。</summary>
    internal IntPtr WindowHandle => new System.Windows.Interop.WindowInteropHelper(_window).Handle;

    /// <summary>关掉所有打开的弹出菜单（Dock/菜单栏被点击时调用）。</summary>
    internal static void CloseAllOpen()
    {
        foreach (var menu in Open.ToList()) menu.CloseAll();
        if (Open.Count == 0) MouseHook.Drop();
    }
    bool _leavingToChild;

    public PopMenu()
    {
        bool dark = Theme.IsDark;
        _card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Background = Frozen(dark ? Color.FromArgb(0xF0, 0x18, 0x18, 0x1C) : Color.FromArgb(0xF2, 0xF7, 0xF7, 0xFA)),
            BorderBrush = Frozen(dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00)),
            Child = new Grid
            {
                Children =
                {
                    _rows,
                    new Border      // 上缘内侧高光：一条 1px 的亮线，玻璃感就靠它
                    {
                        Height = 1,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(8, 1, 8, 0),
                        Background = Frozen(dark ? Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                        IsHitTestVisible = false,
                    },
                },
            },
        };
        _window.Content = _card;
        // 不用 MouseLeave 关菜单：WPF 会在窗口刚显示、指针还不在这块时发一次，菜单会自己秒关。
        _watch.Tick += (_, _) => Watch();
    }

    public event Action? Closed;

    public bool IsOpen => _window.IsVisible;

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Brush HoverBrush => Theme.IsDark ? Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)) : Frozen(Color.FromArgb(0x1C, 0x00, 0x00, 0x00));

    // ── 构建 ──────────────────────────────────────────────────────────────
    public void Add(string header, Action action, bool isChecked = false, bool enabled = true, string? gesture = null)
    {
        var row = Row(header, isChecked, enabled, gesture, out _);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!enabled) return;
            CloseAll();
            action();
        };
        _rows.Children.Add(row);
        _entries.Add((row, null, true));
    }

    public void AddSeparator()
    {
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border { Tag: "sep" }) return;
        var line = new Border
        {
            Height = 1,
            Margin = new Thickness(9, 5, 9, 5),
            Background = Frozen(Theme.IsDark ? Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00)),
            Tag = "sep",
        };
        _rows.Children.Add(line);
    }

    public void AddHeader(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = Typo.Ui,
            FontSize = 11,
            Margin = new Thickness(12, 6, 12, 3),
            Foreground = Frozen(Theme.IsDark ? Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x8C, 0x00, 0x00, 0x00)),
        };
        Typo.Crisp(block);
        _rows.Children.Add(block);
    }

    public PopMenu AddSubmenu(string header)
    {
        var child = new PopMenu { _parent = this };
        child.Closed += () => _leavingToChild = false;

        var row = Row(header, false, true, null, out var chevron);
        chevron.Visibility = Visibility.Visible;
        row.MouseEnter += (_, _) =>
        {
            _leavingToChild = true;
            child.ShowBeside(this, row);
        };

        _rows.Children.Add(row);
        _entries.Add((row, child, false));
        return child;
    }

    Border Row(string header, bool isChecked, bool enabled, string? gesture, out TextBlock chevron)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });

        var check = new TextBlock
        {
            Text = "\u2713",
            FontFamily = Typo.Ui,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isChecked ? Visibility.Visible : Visibility.Hidden,
            Foreground = Frozen(Theme.IsDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x1D, 0x1D, 0x20)),
        };
        Typo.Crisp(check);
        Grid.SetColumn(check, 0);

        var label = new TextBlock
        {
            Text = header,
            FontFamily = Typo.Ui,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Frozen(Theme.IsDark ? Color.FromArgb(0xF2, 0xF2, 0xF5, 0xFF) : Color.FromArgb(0xFF, 0x1D, 0x1D, 0x20)),
            MaxWidth = MaxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Typo.Crisp(label);
        Grid.SetColumn(label, 1);

        var hint = new TextBlock
        {
            Text = gesture ?? "",
            FontFamily = Typo.Ui,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0),
            Foreground = Frozen(Theme.IsDark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
        };
        Typo.Crisp(hint);
        Grid.SetColumn(hint, 2);

        chevron = new TextBlock
        {
            Text = "\u203A",
            FontFamily = Typo.Ui,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
            Foreground = Frozen(Theme.IsDark ? Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xA0, 0x00, 0x00, 0x00)),
        };
        Typo.Crisp(chevron);
        Grid.SetColumn(chevron, 3);

        grid.Children.Add(check);
        grid.Children.Add(label);
        grid.Children.Add(hint);
        grid.Children.Add(chevron);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4, 10, 4),
            Margin = new Thickness(1, 0, 1, 0),
            Opacity = enabled ? 1 : 0.42,
            MinWidth = 200,
        };
        if (enabled)
        {
            row.MouseEnter += (_, _) =>
            {
                row.Background = HoverBrush;
                // 移到本菜单的普通条目上：收起所有子菜单
                foreach (var (_, child, leaf) in _entries)
                    if (leaf) child?.Close();
            };
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        return row;
    }

    // ── 显示 / 关闭 ───────────────────────────────────────────────────────
    /// <summary>在屏幕坐标处弹出（自动夹在工作区内）。</summary>
    public void Show(int x, int y)
    {
        Log.Info($"PopMenu: opened at {x},{y}");
        _window.Show();
        _window.UpdateLayout();

        var work = Displays.FromPoint(new POINT { X = x, Y = y }).Work;
        double scale = _window.PresentationSource()?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double w = _card.ActualWidth * scale, h = _card.ActualHeight * scale;

        // 右下角优先；右边/下边不够就翻到左侧/上方（macOS 也是这样）
        int left = x + 4, top = y + 4;
        if (left + w > work.Right - 6) left = (int)(x - w - 2);
        if (top + h > work.Bottom - 6) top = (int)(work.Bottom - h - 6);
        if (top < work.Top + 6) top = work.Top + 6;
        if (left < work.Left + 6) left = work.Left + 6;

        NativeMethods.SetWindowPos(_window.Handle(), HWND_TOPMOST, left, top, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | 0x0040 /* SWP_SHOWWINDOW */);

        _window.Opacity = 0;
        _window.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
        if (!Open.Contains(this)) Open.Add(this);
        MouseHook.Ensure();
        _watch.Start();
    }

    void ShowBeside(PopMenu parent, Border row)
    {
        if (_window.IsVisible) return;
        var origin = row.PointToScreen(new Point(row.ActualWidth, 0));
        Show((int)origin.X - 4, (int)origin.Y + 6);
    }

    public void Close()
    {
        if (_window.IsVisible) Log.Info($"PopMenu: closed ({_window.ActualWidth:0}x{_window.ActualHeight:0})");
        // 先记账再判断可见性：窗口已经不可见时也要把菜单从 Open 里摘掉，
        // 否则低级鼠标钩子会一直挂着（全系统鼠标输入都要等我们的 UI 线程 → 别的软件像卡死）。
        bool wasOpen = Open.Remove(this);
        if (Open.Count == 0) MouseHook.Drop();
        if (!_window.IsVisible) return;
        _watch.Stop();
        _window.Hide();
        Closed?.Invoke();
        _ = wasOpen;
    }

    void CloseAll()
    {
        for (var m = _parent; m is not null; m = m._parent) m.Close();
        Close();
    }

    void Watch()
    {
        if (!_window.IsVisible) { _watch.Stop(); return; }
        if ((GetAsyncKeyState(0x1B /* VK_ESCAPE */) & 0x8000) != 0) { CloseAll(); return; }

        // 不再按鼠标按键状态自动关：这里误判过一次（菜单弹出即消失），代价比"菜单多留一会儿"大。
        // 关闭途径：Esc、点菜单项、点 Dock/菜单栏上的任意位置（见 DockWindow 的鼠标抬起处理）。
    }

    bool IsOverAny()
    {
        if (!GetCursorPos(out var p)) return true;
        for (PopMenu? m = this; m is not null; m = m._parent)
        {
            if (!m._window.IsVisible) continue;
            var origin = m._window.PointToScreen(new Point(0, 0));
            if (p.X >= origin.X && p.X <= origin.X + m._window.ActualWidth * Dpi(m._window)
                && p.Y >= origin.Y && p.Y <= origin.Y + m._window.ActualHeight * Dpi(m._window)) return true;
        }
        return false;
    }

    static double Dpi(Window w) => w.PresentationSource()?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    void ScheduleCloseIfNotOverChild()
    {
        // 指针正移向子菜单：给它一点时间，别自己先关掉
        if (_leavingToChild) return;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
        {
            if (_leavingToChild) return;
            if (!IsOverAny()) Close();
        }), DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _watch.Stop();
        _window.Close();
    }
}

internal static class WindowExtensions
{
    public static PresentationSource? PresentationSource(this Window window) =>
        System.Windows.PresentationSource.FromVisual(window);

    public static IntPtr Handle(this Window window) =>
        new System.Windows.Interop.WindowInteropHelper(window).Handle;
}

/// <summary>
/// 低级鼠标钩子：用来实现"点菜单外面就关闭"。
/// 轮询 GetAsyncKeyState 判断按下不可靠（会把刚弹出的菜单自己关掉，或干脆不关），这里改用系统钩子。
/// </summary>
internal static class MouseHook
{
    const int WH_MOUSE_LL = 14;
    const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    static IntPtr _hook;
    static HookProc? _proc;   // 必须保住委托，否则会被 GC 回收导致钩子失效

    public static void Ensure()
    {
        if (_hook != IntPtr.Zero) return;
        _proc ??= Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Info("PopMenu: the mouse hook was refused; Esc still closes the menu");
    }

    /// <summary>没有任何菜单开着却还挂着钩子时，强制卸掉（定时器每秒调一次，开销可忽略）。</summary>
    public static void DropIfIdle()
    {
        if (_hook != IntPtr.Zero && PopMenu.OpenMenus.Count == 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Info("PopMenu: dropped a stale mouse hook");
        }
    }

    public static void Drop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int message = (int)wParam;
                if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (!OverAnyMenu(info.pt))
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher is not null) dispatcher.BeginInvoke(new Action(PopMenu.CloseAllOpen));
                    }
                }
            }
        }
        catch (Exception)
        {
            // 钩子回调里绝不能抛
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool OverAnyMenu(POINT p)
    {
        foreach (var menu in PopMenu.OpenMenus)
        {
            var hwnd = menu.WindowHandle;
            if (hwnd == IntPtr.Zero) continue;
            if (!GetWindowRect(hwnd, out var r)) continue;
            if (p.X >= r.L && p.X <= r.R && p.Y >= r.T && p.Y <= r.B) return true;
        }
        return false;
    }
}
