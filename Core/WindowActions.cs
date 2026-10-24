using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

internal static class WindowActions
{
    /// <summary>Restores (if minimized) and brings a window to the front.</summary>
    public static void Activate(IntPtr hwnd)
    {
        // 句柄可能是托盘消息窗口 / tooltip 这类假窗口：先按进程把真正的主窗口叫回来。
        if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != 0 && SurfaceHiddenWindowsOfPid(owner)) return;
        }
        if (IsIconic(hwnd))
        {
            // ShowWindowAsync 对部分程序不起作用（任务管理器就是其中之一）：同步 ShowWindow 再补一条 SC_RESTORE。
            ShowWindow(hwnd, SW_RESTORE);
            if (IsIconic(hwnd)) PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)0xF120 /* SC_RESTORE */, IntPtr.Zero);
        }
        // Allowed because the click on the dock was the last input; the fallback covers the rest.
        if (!SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
    }

    /// <summary>Minimizes through the window's own system command, so the app sees it and animates.</summary>
    public static void Minimize(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, SC_MINIMIZE, IntPtr.Zero);

    /// <summary>
    /// 把 <paramref name="executable"/> 对应进程里"藏着"的顶层窗口显示出来并激活（缩到托盘/最小化时隐藏）。
    /// 返回 true 表示已经有一个窗口被叫回来了，调用方就不要再启动新实例。
    /// </summary>
    /// <summary>把某个可执行文件对应进程里 隐藏/最小化 的顶层窗口叫回来；true = 已经叫回来了，不要再起新实例。</summary>
    public static bool SurfaceHiddenWindows(string executable)
    {
        if (string.IsNullOrEmpty(executable)) return false;
        string stem = System.IO.Path.GetFileNameWithoutExtension(executable);
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(stem))
        {
            try
            {
                if (!string.IsNullOrEmpty(process.MainModule?.FileName)
                    && !string.Equals(process.MainModule!.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch (Exception)
            {
                // 提权进程读不到路径：按进程名当候选
            }
            if (SurfaceHiddenWindowsOfPid((uint)process.Id)) return true;
        }
        return false;
    }

    /// <summary>按进程 id 把藏着或最小化的顶层窗口叫回来（跳过工具窗口与无标题的内部窗口）。</summary>
    public static bool SurfaceHiddenWindowsOfPid(uint pid)
    {
        var revealed = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != pid) return true;
            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((ex & 0x80L) != 0) return true;                 // WS_EX_TOOLWINDOW
            if (GetWindowTextLength(hwnd) == 0) return true;      // 内部窗口
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) revealed.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        if (revealed.Count == 0) return false;
        foreach (var hwnd in revealed)
        {
            ShowWindowAsync(hwnd, SW_RESTORE);
            ShowWindowAsync(hwnd, 5);   // SW_SHOW
        }
        if (!SetForegroundWindow(revealed[0])) SwitchToThisWindow(revealed[0], true);

        // 提权进程（UIPI）会无视我们的 ShowWindow：确认一下有没有真的显示出来，把结论写进日志。
        bool shown = revealed.Exists(h => IsWindowVisible(h) && !IsIconic(h));
        if (shown) Log.Info($"Surfaced {revealed.Count} window(s) of pid {pid}");
        else Log.Info($"Cannot surface pid {pid}: the window belongs to an elevated process (UIPI blocks it)");
        return true;
    }


    /// <summary>Asks the window to close, exactly like its close button.</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, SC_CLOSE, IntPtr.Zero);
}
