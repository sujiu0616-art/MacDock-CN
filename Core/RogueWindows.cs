using System.Runtime.InteropServices;
using System.Text;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

/// <summary>
/// 清掉"赖在菜单栏那条上的别人的小窗口"。
///
/// 真实案例：Firefox 会创建一个 <c>MozillaBatteryClass</c>、标题 "Battery Watcher"、
/// 大小 135×37、位置 (0,0) 的窗口 —— 本该隐藏，却一直可见。它正好压在我们菜单栏的左上角，
/// 而我们菜单栏是半透明的，于是"Battery Watcher"就透在应用名底下，
/// 看着像应用名后面残留了一段灰字。实测其它应用也可能留同类小窗。
///
/// 处理方式：只隐藏"完全落在菜单栏这条里、且尺寸很小"的窗口，不碰任何正常窗口。
/// 每 2 秒扫一次（与全屏检测同一个心跳），发现就藏，每个窗口只记一次日志。
/// </summary>
internal static class RogueWindows
{
    static readonly HashSet<IntPtr> _hidden = new();

    public static void Sweep(int barHeightPx)
    {
        try
        {
            var mine = Environment.ProcessId;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var rect)) return true;

                int width = rect.Width, height = rect.Height;
                if (width <= 0 || height <= 0) return true;

                // 只在菜单栏这条区域里动手
                if (rect.Top < -2 || rect.Bottom > barHeightPx + 4) return true;
                if (width > 320 || height > barHeightPx + 4) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == mine) return true;

                string className = GetClassName(hwnd);
                if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW"
                    or "EdgeUiInputTopWndClass" or "ForegroundStaging") return true;

                // Firefox 的电池窗口是已知案例；其它任何"贴在这一条里的小窗"也藏掉
                ShowWindow(hwnd, SW_HIDE);
                if (_hidden.Add(hwnd))
                    Log.Info($"Rogue window hidden: {className} at ({rect.Left},{rect.Top}) {width}x{height}");
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Hiding rogue windows over the menu bar");
        }
    }

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
}
