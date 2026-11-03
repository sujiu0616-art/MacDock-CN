using MacDock.Core;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 微信的新消息卡片（窗口类 <c>Qt51514QWindowToolSaveBits</c>，属于 Weixin.exe）是它自己按托盘图标
/// 算位置的：实测弹出来是物理像素 (2160,11) 435x312 —— 顶边 11 压在菜单栏（48px 高）里、
/// 右边 2595 还超出屏幕 35px。它自己不会收回来。
///
/// 做法：挂 EVENT_OBJECT_SHOW，卡片一显示就在同一帧里摆到"菜单栏下方、右侧留 8px"，
/// 而不是靠轮询（轮询会让用户看见它先出现在错的位置再跳过来 —— 那是"修正行为"，不是位置正确）。
/// 之后 16ms 复核 14 次，抵消微信自己的入场动画；再不行还有低频兜底扫描。
///
/// 为什么不去改"它算位置时用的输入"：微信取的是 Windows 托盘那套信息（托盘在顶部是我们自己的
/// 设计），把它改到屏幕底部会连带影响所有托盘应用的右键菜单锚点。只作用于这一种卡片最干净。
/// </summary>
internal static class WeChatNotice
{
    const string WindowClass = "Qt51514QWindowToolSaveBits";
    const int EdgeGap = 8;    // 离屏幕右边缘
    const int TopGap = 6;     // 离菜单栏下沿

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    static IntPtr _hook;
    static WinEventProc? _proc;                 // 原生侧会回调，委托必须留引用
    static readonly HashSet<IntPtr> Placed = [];

    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnShow;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log.Info(_hook == IntPtr.Zero
            ? "WeChat notice: could not watch for cards"
            : "WeChat notice: watching for notification cards");
    }

    static void OnShow(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || hwnd == IntPtr.Zero) return;
        if (GetClassName(hwnd) != WindowClass) return;
        // 钩子回调必须尽快返回：真正的搬窗口丢回 UI 线程，优先级 Send（先于重绘）
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() => Place(hwnd)), System.Windows.Threading.DispatcherPriority.Send);
    }

    static void Place(IntPtr hwnd)
    {
        Apply(hwnd);
        // 微信随后可能自己再移动一次（入场动画）。复核十几帧，之后就交给它自己。
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        int left = 14;
        timer.Tick += (_, _) =>
        {
            if (--left <= 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd)) { timer.Stop(); return; }
            Apply(hwnd);
        };
        timer.Start();
    }

    static void Apply(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return;
        if (!GetWindowRect(hwnd, out var rect)) return;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        var display = Displays.ByNameOrPrimary(null);
        int menuBottom = display.Bounds.Top + (int)Math.Ceiling(MenuBarWindow.BarHeight * display.Scale);
        int left = display.Bounds.Right - width - EdgeGap;
        int top = menuBottom + TopGap;
        if (rect.Left == left && rect.Top == top) return;

        SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (Placed.Add(hwnd))
            Log.Info($"WeChat notice: placed at ({left},{top}) {width}x{height} (it had put itself at {rect.Left},{rect.Top})");
    }

    /// <summary>兜底：钩子万一丢了或者事件没到，低频扫一遍桌面上的卡片。</summary>
    public static void Nudge()
    {
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetClassName(hwnd) == WindowClass) Apply(hwnd);
            return true;
        }, IntPtr.Zero);
        if (Placed.Count > 32) Placed.Clear();   // hwnd 会被复用，别无限攒
    }
}
