using System.Runtime.InteropServices;
using System.Text;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

/// <summary>
/// 守住菜单栏那条"我们的地盘"：任何窗口一旦出现在这条里，立刻隐藏。
///
/// 为什么需要（真实案例）：Firefox 会创建一个 <c>MozillaBatteryClass</c>、标题 "Battery Watcher"、
/// 位置 (0,0)、大小 135×37 的顶层窗口，本该隐藏却一直可见。它正好压在我们**半透明**的菜单栏下面，
/// 于是"Battery Watcher"透出来，看起来像应用名后面糊了一段灰字。别的程序也可能有同类小窗。
///
/// 为什么不按"进程/类名"打补丁：那是修表面 —— 换个程序、换个版本又会出现。
/// 这里的规则只有一条：**这块矩形是我们向系统注册的 appbar，属于外壳，任何别的东西出现在里面就移走**。
///
/// 实现：<c>SetWinEventHook</c> 监听 EVENT_OBJECT_SHOW / EVENT_OBJECT_LOCATIONCHANGE（毫秒级），
/// 外加每 2 秒一次全盘扫描兜底（钩子装好之前就存在的窗口、以及不触发事件的移动）。
/// </summary>
internal static class StripGuard
{
    const uint EVENT_OBJECT_SHOW = 0x8002;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const int OBJID_WINDOW = 0;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    static readonly HashSet<IntPtr> _hidden = new();
    static WinEventProc? _proc;          // 必须保住委托，否则被 GC 回收
    static IntPtr _showHook, _moveHook;
    static RECT _strip;

    delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>菜单栏占据的屏幕矩形（物理像素）。布局变化时调用，钩子只装一次。</summary>
    public static void Update(RECT strip)
    {
        _strip = strip;
        if (_showHook != IntPtr.Zero) return;
        _proc ??= OnWinEvent;
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_showHook == IntPtr.Zero) Log.Info("StripGuard: the show hook was refused (falling back to polling)");
    }

    public static void Uninstall()
    {
        if (_showHook != IntPtr.Zero) { UnhookWinEvent(_showHook); _showHook = IntPtr.Zero; }
        if (_moveHook != IntPtr.Zero) { UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; }
    }

    static void OnWinEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        Evict(hwnd, eventId == EVENT_OBJECT_SHOW ? "shown" : "moved");
    }

    /// <summary>兜底扫描：钩子装好之前就存在的窗口、以及不触发事件的窗口。</summary>
    public static void Sweep()
    {
        try
        {
            EnumWindows((hwnd, _) =>
            {
                Evict(hwnd, "sweep");
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Sweeping the menu bar strip");
        }
    }

    /// <summary>窗口若落在我们的菜单栏条里就隐藏它。返回是否动手。</summary>
    static bool Evict(IntPtr hwnd, string reason)
    {
        try
        {
            if (!IsWindowVisible(hwnd)) return false;
            if (!GetWindowRect(hwnd, out var rect)) return false;
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            // 必须完全落在我们的条里（半像素允许），否则不碰
            if (rect.Left < _strip.Left - 2 || rect.Right > _strip.Right + 2) return false;
            if (rect.Top < _strip.Top - 2 || rect.Bottom > _strip.Bottom + 2) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) return false;

            string className = GetClassName(hwnd);
            // 系统外壳类不碰：桌面、任务栏、输入法候选、开始菜单宿主、通知宿主
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
                or "EdgeUiInputTopWndClass" or "ForegroundStaging" or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "TaskSwitcherWnd"
                or "ApplicationFrameWindow" or "tooltips_class32" or "Shell_InputSwitchTopLevelWindow") return false;
            if (className.Contains("Toast", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Ime", StringComparison.OrdinalIgnoreCase)) return false;

            ShowWindow(hwnd, SW_HIDE);
            if (_hidden.Add(hwnd))
                Log.Info($"StripGuard: hid {className} at ({rect.Left},{rect.Top}) {rect.Width}x{rect.Height} ({reason})");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
}
