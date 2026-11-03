using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using MacDock.Core;
using MacDock.Interop;
using ManagedShell.WindowsTray;
using static MacDock.Interop.NativeMethods;
using MsNative = ManagedShell.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// Third-party notification area icons, received through ManagedShell's tray service (which
/// forwards everything to Explorer as well, so its own tray stays complete). Clicks, double
/// clicks and hovers go to the owning app exactly as Explorer would send them.
/// </summary>
internal sealed class TrayIconsPanel : StackPanel, IDisposable
{
    // Explorer's own icons: the menu bar draws Wi-Fi, volume and battery itself.
    static readonly HashSet<Guid> SystemIcons =
    [
        new(NotificationArea.NETWORK_GUID), new(NotificationArea.POWER_GUID), new(NotificationArea.VOLUME_GUID),
    ];

    readonly TrayService _trayService = new();
    readonly NotificationArea _area;
    readonly DockConfig _config;
    // By reference: NotifyIcon equality compares its (changing) window handle, id and GUID.
    bool _pixelLogged;

    readonly Dictionary<NotifyIcon, Border> _views = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<NotifyIcon> _subscribed = new(ReferenceEqualityComparer.Instance);
    bool _disposed;

    public TrayIconsPanel(DockConfig config)
    {
        _config = config;
        Orientation = Orientation.Horizontal;
        _area = new NotificationArea(NotificationArea.DEFAULT_PINNED, _trayService, new ExplorerTrayService())
        {
            // The default is one collection shared by every NotificationArea: after the menu bar is
            // turned off and on it would still hold the icons of apps closed in between.
            TrayIcons = [],
        };
        _area.TrayIcons.CollectionChanged += OnCollectionChanged;
        _area.SetAppBarMessageCallback(AppBarRelay.Handle);
        // Broadcasts TaskbarCreated: every app re-adds its icon, now to us first.
        _area.Initialize();
        if (_area.IsFailed) Log.Info("Notification area: failed to start");
        Sync();

        // 开机时"隐藏 Explorer 任务栏"现在排在托盘挂钩之前，个别程序可能在我们建好托盘窗口前就把
        // 图标注册给了正被隐藏的 Explorer：这里补一次广播，让它们重新注册到我们这里。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            int count = _area.TrayIcons.Count;
            Log.Info($"Notification area: {count} tray icon(s) after init");
            if (count == 0)
            {
                uint message = RegisterWindowMessage("TaskbarCreated");
                if (message != 0) BroadcastTaskbarCreated(message);
                Log.Info("Notification area: broadcast TaskbarCreated again (no icons yet)");
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Tells the tray where it lives, so apps place their menus and flyouts next to it.</summary>
    public void SetHostRect(RECT bar)
    {
        _area.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = MsNative.ABEdge.ABE_TOP,
            rc = new MsNative.Rect { Left = bar.Left, Top = bar.Top, Right = bar.Right, Bottom = bar.Bottom },
        });
    }

    public Brush HoverBrush { get; set; } = Brushes.Transparent;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _area.TrayIcons.CollectionChanged -= OnCollectionChanged;
        foreach (var icon in _subscribed) icon.PropertyChanged -= OnIconChanged;
        _subscribed.Clear();
        _views.Clear();
        _area.Dispose();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Sync();

    void OnIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotifyIcon icon) return;
        switch (e.PropertyName)
        {
            case nameof(NotifyIcon.Icon) when _views.TryGetValue(icon, out var view):
                ((Image)view.Child).Source = Trimmed(icon.Icon);
                break;
            case nameof(NotifyIcon.Title) when _views.TryGetValue(icon, out var view):
                view.Tag = Tip(icon);   // 名字变了下次悬停时取新值
                break;
            case nameof(NotifyIcon.IsHidden):
                // 某些程序会把图标反复隐藏/显示（比如播放器的"仅在有提示时出现"）：合成一次重建，
                // 别每次属性变化都 Children.Clear() 整排重来 —— 那是空闲 CPU 的大头。
                ScheduleSync();
                break;
        }
    }

    bool _syncPending;

    void ScheduleSync()
    {
        if (_syncPending) return;
        _syncPending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _syncPending = false;
            Sync();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Brings the row of views in line with the tray's icons, keeping existing views (and their hover state).</summary>
    internal static int DiagSyncs;

    void Sync()
    {
        DiagSyncs++;
        var wanted = _area.TrayIcons.Where(i => !i.IsHidden && !SystemIcons.Contains(i.GUID)).ToList();

        // 用户拖出来的顺序优先；没在顺序表里的图标保持系统给的次序，排在后面。
        // OrderBy 是稳定排序，所以"没排序过"时这一整段等价于原样。
        var saved = _config?.Settings.TrayOrder;
        if (saved is { Count: > 0 })
        {
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < saved.Count; i++)
                if (!string.IsNullOrEmpty(saved[i])) rank[saved[i]] = i;
            wanted = wanted.OrderBy(i => rank.TryGetValue(KeyOf(i), out int r) ? r : int.MaxValue).ToList();
        }

        // Hidden icons stay subscribed: they can be shown again (NIS_HIDDEN cleared).
        foreach (var icon in _area.TrayIcons)
            if (_subscribed.Add(icon)) icon.PropertyChanged += OnIconChanged;
        foreach (var gone in _subscribed.Where(i => !_area.TrayIcons.Contains(i)).ToList())
        {
            gone.PropertyChanged -= OnIconChanged;
            _subscribed.Remove(gone);
        }
        foreach (var gone in _views.Keys.Where(i => !wanted.Contains(i)).ToList()) _views.Remove(gone);

        // 顺序/数量没变就别动视觉树：Children.Clear() + 重建会触发一整轮布局与合成。
        if (Children.Count == wanted.Count)
        {
            bool same = true;
            for (int i = 0; i < wanted.Count; i++)
            {
                if (!_views.TryGetValue(wanted[i], out var existing) || !ReferenceEquals(Children[i], existing))
                {
                    same = false;
                    break;
                }
            }
            if (same) return;
        }

        Children.Clear();
        foreach (var icon in wanted)
        {
            if (!_views.TryGetValue(icon, out var view)) _views[icon] = view = CreateView(icon);
            Children.Add(view);
        }
    }

    // macOS 菜单栏图标按 18pt 画；进来的是 48x48 的位图，先裁掉透明边再按这个尺寸等比显示。
    // 不裁的话"满画布"的图标和"只占中间一小块"的图标会显得一大一小，一排看过去参差不齐。
    const double IconSizeDiu = 18;
    const double IconGapDiu = 4;   // 每个图标左右各 4 DIU ⇒ 相邻图标间 8 DIU，接近 mac 的呼吸感

    /// <summary>裁掉图标四周的透明像素。不是位图、读不出来、已经满画布、或整张全透明时原样返回。</summary>
    static ImageSource? Trimmed(ImageSource? source)
    {
        if (source is not System.Windows.Media.Imaging.BitmapSource sourceBitmap) return source;
        try
        {
            var bgra = sourceBitmap.Format == PixelFormats.Bgra32
                ? sourceBitmap
                : new System.Windows.Media.Imaging.FormatConvertedBitmap(sourceBitmap, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w <= 0 || h <= 0 || w > 512 || h > 512) return sourceBitmap;

            int stride = w * 4;
            var pixels = new byte[stride * h];
            bgra.CopyPixels(pixels, stride, 0);

            int left = w, top = h, right = -1, bottom = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (pixels[row + x * 4 + 3] < 8) continue;   // 只看 alpha：托盘图标普遍带透明外框
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
            if (right < left || bottom < top) return sourceBitmap;                                   // 整张全透明
            if (left == 0 && top == 0 && right == w - 1 && bottom == h - 1) return sourceBitmap;     // 已经是满画布

            var crop = new System.Windows.Media.Imaging.CroppedBitmap(
                bgra, new Int32Rect(left, top, right - left + 1, bottom - top + 1));
            crop.Freeze();
            return crop;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Trimming a tray icon");
            return sourceBitmap;
        }
    }

    // ── 拖动排序（macOS 菜单栏也是把图标拖来拖去） ──
    const double DragThreshold = 6;   // DIU：横向移动超过它才算拖动，否则仍当单击
    Border? _pressView;
    NotifyIcon? _pressIcon;
    Point _pressPoint;
    TranslateTransform? _dragTransform;
    bool _dragging;

    /// <summary>排序用的稳定标识：优先图标自己的 GUID，老式图标（无 GUID）退回应用名。</summary>
    static string KeyOf(NotifyIcon icon) =>
        icon.GUID != Guid.Empty ? "guid:" + icon.GUID.ToString("N") : "title:" + (icon.Title ?? "");

    void BeginDrag(Border view)
    {
        _dragging = true;
        _dragTransform = new TranslateTransform();
        view.RenderTransform = _dragTransform;
        view.Opacity = 0.65;
        Log.Info($"Tray: dragging <{_pressIcon?.Title}>");
    }

    /// <summary>松开：按落点算出目标索引，重排并存进配置（下次启动仍然按这个顺序）。</summary>
    void EndDrag(Point release)
    {
        if (_pressView is null) return;
        var view = _pressView;

        int current = Children.IndexOf(view);
        int target = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            if (Children[i] is not Border other || ReferenceEquals(other, view)) continue;
            double middle = other.TranslatePoint(new Point(other.ActualWidth / 2, 0), this).X;
            if (release.X > middle) target = i + 1;
        }
        if (target != current && target != current + 1)
        {
            Children.Remove(view);
            Children.Insert(Math.Clamp(target > current ? target - 1 : target, 0, Children.Count), view);
            SaveOrder();
        }

        view.RenderTransform = Transform.Identity;
        view.Opacity = 1;
        _dragTransform = null;
    }

    void SaveOrder()
    {
        try
        {
            var order = new List<string>();
            foreach (var border in Children.OfType<Border>())
            {
                foreach (var pair in _views)
                {
                    if (!ReferenceEquals(pair.Value, border)) continue;
                    order.Add(KeyOf(pair.Key));
                    break;
                }
            }
            _config.Settings.TrayOrder = order;
            _config.Save();
            Log.Info("Tray: icon order saved - " + string.Join(", ", order));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the tray icon order");
        }
    }

    // ── 原生菜单"点了外面要收" ──
    // 微信这类 Qt 程序的托盘菜单是它自己进程里弹出的原生菜单（类名 #32768），owner 是它那个隐藏的
    // 消息窗口；前台又不在它手上（我们的菜单栏是 WS_EX_NOACTIVATE，SetForegroundWindow 对它无效），
    // 于是用户点菜单外面时，系统不会替它把菜单收掉 —— 菜单就赖在屏幕上。
    // 这里补一刀：菜单出现后盯住鼠标，一旦在菜单矩形外按下，就主动让菜单取消。
    const uint WM_CANCELMODE = 0x001F, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_ESCAPE = 0x1B;
    static bool _menuWatchActive;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    static void WatchNativeMenu()
    {
        if (_menuWatchActive) return;
        _menuWatchActive = true;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        IntPtr menu = IntPtr.Zero;
        RECT rect = default;
        long started = Environment.TickCount64;

        timer.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - started > 30000) { timer.Stop(); _menuWatchActive = false; return; }
            if (menu == IntPtr.Zero)
            {
                menu = FindWindow("#32768", null);      // 原生弹出菜单的系统类名
                if (menu == IntPtr.Zero) return;         // 还没弹出来
                GetWindowRect(menu, out rect);
                return;
            }
            if (!IsWindow(menu) || !IsWindowVisible(menu)) { timer.Stop(); _menuWatchActive = false; return; }
            if (!GetCursorPos(out var cursor)) return;
            bool inside = cursor.X >= rect.Left && cursor.X < rect.Right
                       && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
            if (inside) return;
            bool pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
            if (!pressed) return;

            Log.Info("Tray: the click outside did not dismiss the native menu - cancelling it");
            PostMessage(menu, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
            PostMessage(menu, WM_KEYDOWN, VK_ESCAPE, IntPtr.Zero);
            PostMessage(menu, WM_KEYUP, VK_ESCAPE, IntPtr.Zero);
            timer.Stop();
            _menuWatchActive = false;
        };
        timer.Start();
    }

    Border CreateView(NotifyIcon icon)
    {
        if (icon.Icon is System.Windows.Media.Imaging.BitmapSource probe && !_pixelLogged)
        {
            _pixelLogged = true;
            Log.Info($"TrayIcons: pixel size {probe.PixelWidth}x{probe.PixelHeight} (source {probe.GetType().Name})");
        }
        var image = new Image
        {
            Source = Trimmed(icon.Icon),
            Height = IconSizeDiu,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var view = new Border
        {
            Child = image,
            // 固定宽度而不是靠 Padding：每个托盘图标的"内容"宽度天差地别（有的满画布、有的只占中间一块），
            // 用等宽容器 + 图标居中，间距才会像 mac 菜单栏那样均匀。
            Width = IconSizeDiu + IconGapDiu * 2,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        view.MouseEnter += (_, _) =>
        {
            if (_dragging) return;
            view.Background = HoverBrush;
            NameTip.ShowFor(view, Tip(icon));
            SetPlacement(icon, view);
            icon.IconMouseEnter(MouseParam());
        };
        view.MouseLeave += (_, _) =>
        {
            view.Background = Brushes.Transparent;
            NameTip.HideTip();
            if (!_dragging) icon.IconMouseLeave(MouseParam());
        };
        view.MouseMove += (_, e) =>
        {
            if (ReferenceEquals(_pressView, view) && !_dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                var p = e.GetPosition(this);
                if (Math.Abs(p.X - _pressPoint.X) >= DragThreshold) BeginDrag(view);
            }
            if (_dragging)
            {
                if (_dragTransform is not null) _dragTransform.X = e.GetPosition(this).X - _pressPoint.X;
                return;   // 拖动中不把移动转给图标宿主，免得它以为用户在它身上滑
            }
            icon.IconMouseMove(MouseParam());
        };
        view.MouseDown += (_, e) =>
        {
            SetPlacement(icon, view);
            if (e.ChangedButton == MouseButton.Left)
            {
                // 左键先不转发：这一次可能是"把图标拖到别处"。单击的 down/up 在松开时补发，
                // 托盘程序绝大多数在 up 里才动作，补发不会改变它们的行为。
                _pressView = view;
                _pressIcon = icon;
                _pressPoint = e.GetPosition(this);
                _dragging = false;
                view.CaptureMouse();
            }
            else
            {
                icon.IconMouseDown(e.ChangedButton, MouseParam(), (int)GetDoubleClickTime());
            }
            e.Handled = true;
        };
        view.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                bool dragged = _dragging;
                if (dragged) EndDrag(e.GetPosition(this));
                else
                {
                    int dbl = (int)GetDoubleClickTime();
                    icon.IconMouseDown(MouseButton.Left, MouseParam(), dbl);
                    icon.IconMouseUp(MouseButton.Left, MouseParam(), dbl);
                }
                _pressView = null;
                _pressIcon = null;
                _dragging = false;
                _dragTransform = null;
                view.ReleaseMouseCapture();
                if (!dragged)
                {
                    // 微信（Qt）对托盘单击经常"吞掉"：主窗口不恢复。这里兜底把它拉出来。
                    if (MacDock.Core.WeChatRestorer.LooksLikeWeChat(icon.Title))
                    {
                        var restore = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                        restore.Tick += (_, _) =>
                        {
                            restore.Stop();
                            MacDock.Core.WeChatRestorer.Restore();
                        };
                        restore.Start();
                    }
                    // 200ms 后清理"应用的托盘消息窗口"（微信的 WxTrayIconMessageWindow 会被自己弹出来）
                    var hygiene = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    hygiene.Tick += (_, _) => { hygiene.Stop(); HideStrayTrayWindows(); };
                    hygiene.Start();
                }
                e.Handled = true;
                return;
            }

            // 托盘程序的弹出菜单（微信/Qt 那类）在自己线程里 TrackPopupMenu，
            // 前提是"这次点击发生在某个前台窗口上"。Explorer 的托盘天生满足（任务栏就是前台），
            // 我们的菜单栏不是 —— 于是菜单不出现。先把菜单栏设成前台，再把点击转给图标宿主。
            var bar = Window.GetWindow(view);
            if (bar is not null) NativeMethods.SetForegroundWindow(new WindowInteropHelper(bar).Handle);
            SetPlacement(icon, view);   // 右键前重算：应用拿这个矩形算菜单锚点
            // Windows 的前台锁：非前台进程调 TrackPopupMenu 会被直接挡掉。
            // 把"允许前台"的令牌交出去，右键菜单才有可能真正弹出来（微信/Qt/网易云这类都吃这条）。
            try { AllowSetForegroundWindow(ASFW_ANY); } catch (Exception) { }
            if (e.ChangedButton == MouseButton.Right)
                Log.Info("Tray: right-click on <" + icon.Title + "> - forwarding to icon host" +
                         (Environment.GetCommandLineArgs().Contains("--tipdiag")
                             ? $" placement=({icon.Placement.Left},{icon.Placement.Top})-({icon.Placement.Right},{icon.Placement.Bottom})"
                             : ""));
            icon.IconMouseUp(e.ChangedButton, MouseParam(), (int)GetDoubleClickTime());
            WatchNativeMenu();   // 原生菜单可能不会自己收（owner 不在前台），补一刀
            var trayHygiene = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            trayHygiene.Tick += (_, _) => { trayHygiene.Stop(); HideStrayTrayWindows(); };
            trayHygiene.Start();
            e.Handled = true;
        };
        return view;
    }

    static string? Tip(NotifyIcon icon) => string.IsNullOrWhiteSpace(icon.Title) ? null : icon.Title;

    /// <summary>The icon's rectangle on screen, in physical pixels (Shell_NotifyIconGetRect answers with it).</summary>
    /// <summary>
    /// 告诉 ManagedShell 这个托盘图标在屏幕上的矩形 —— 应用（微信/Qt、QQ 等）用它算右键菜单锚点。
    /// 之前菜单跑到屏幕左下角，就是因为视图还没完成布局时这里给出 (0,0)-(0,0) 的退化矩形，
    /// 应用只好把菜单夹到可见区角落。
    /// </summary>
    void SetPlacement(NotifyIcon icon, FrameworkElement view)
    {
        if (PresentationSource.FromVisual(view) is null) return;

        var topLeft = view.PointToScreen(new Point(0, 0));
        var bottomRight = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
        int left = (int)Math.Round(topLeft.X), top = (int)Math.Round(topLeft.Y);
        int right = (int)Math.Round(bottomRight.X), bottom = (int)Math.Round(bottomRight.Y);

        if (right - left <= 0 || bottom - top <= 0)
        {
            if (!GetCursorPos(out var cursor)) return;
            left = cursor.X; right = cursor.X + 1;
            top = cursor.Y; bottom = cursor.Y + 1;
        }

        icon.Placement = new MsNative.Rect { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    /// <summary>The cursor position packed as the tray callbacks expect it (x low word, y high word).</summary>
    /// <summary>隐藏托盘应用的消息窗口（类名含 TrayIconMessageWindow 的可见空白窗）。</summary>
    static void HideStrayTrayWindows()
    {
        try
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    var cls = new System.Text.StringBuilder(128);
                    NativeMethods.GetClassName(hwnd, cls, 128);
                    string name = cls.ToString();
                    if (name.IndexOf("TrayIconMessageWindow", StringComparison.OrdinalIgnoreCase) < 0) return true;
                    if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                    NativeMethods.ShowWindow(hwnd, 0);   // SW_HIDE
                    Log.Info("Tray: hid stray message window " + name);
                    return true;
                }, IntPtr.Zero);
            });
        }
        catch (Exception ex) { Log.Info("Tray: hide stray failed - " + ex.Message); }
    }

    static uint MouseParam()
    {
        GetCursorPos(out var p);
        return (uint)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
    }
    const int ASFW_ANY = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern uint RegisterWindowMessage(string name);

    /// <summary>广播 TaskbarCreated：让所有托盘程序把图标重新注册到我们这里。</summary>
    static void BroadcastTaskbarCreated(uint message) =>
        SendNotifyMessage((IntPtr)0xFFFF, message, IntPtr.Zero, IntPtr.Zero);   // HWND_BROADCAST
}
