using MacDock.Core;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 微信的新消息卡片（窗口类 <c>Qt51514QWindowToolSaveBits</c>，属于 Weixin.exe）是它自己按托盘图标
/// 算位置的：实测弹出来是物理像素 (2160,11) 435x312 —— 顶边 11 压在菜单栏（48px 高）里、
/// 右边 2595 还超出屏幕 35px。它自己不会收回来，这里在卡片第一次出现时把它摆到
/// "菜单栏下方、右侧留 8px"，尺寸一点不动。
///
/// 为什么不由我们去改"它算位置时用的输入"：微信拿的是 Windows 托盘那套位置（托盘在顶部是我们
/// 自己的设计），把托盘信息改成底部会连带影响所有托盘应用的右键菜单锚点。挪窗口是唯一只作用在
/// 这一种卡片上的做法。
/// </summary>
internal static class WeChatNotice
{
    const string WindowClass = "Qt51514QWindowToolSaveBits";
    const int EdgeGap = 8;    // 离屏幕右边缘
    const int TopGap = 6;     // 离菜单栏下沿

    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    static readonly HashSet<IntPtr> Moved = [];

    /// <summary>把摆错位置的微信卡片拉回屏幕内。每个卡片窗口只处理一次（它会播入场动画）。</summary>
    public static void Nudge()
    {
        var display = Displays.ByNameOrPrimary(null);
        int menuBottom = display.Bounds.Top + (int)Math.Ceiling(MenuBarWindow.BarHeight * display.Scale);
        int right = display.Bounds.Right;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetClassName(hwnd) != WindowClass) return true;
            if (!Moved.Add(hwnd)) return true;                       // 同一个窗口只摆一次
            if (!GetWindowRect(hwnd, out var rect)) return true;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return true;

            int left = right - width - EdgeGap;
            int top = menuBottom + TopGap;
            if (rect.Left == left && rect.Top == top) return true;
            if (rect.Left >= display.Bounds.Left && rect.Top >= menuBottom && rect.Right <= right) return true;  // 本来就摆对了

            SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Log.Info($"WeChat notice: moved ({rect.Left},{rect.Top}) {width}x{height} -> ({left},{top})");
            return true;
        }, IntPtr.Zero);

        if (Moved.Count > 32) Moved.Clear();   // hwnd 会被复用，别无限攒
    }
}
