using System;
using System.Runtime.InteropServices;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

internal static class WindowActions
{
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
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
        ForceForeground(hwnd);
    }

    /// <summary>
    /// 逐级降级地把窗口切到前台。Windows 的前台锁会让 SetForegroundWindow 直接返回 false，
    /// 单靠它 + SwitchToThisWindow 会"点了没反应"（用户会描述成应用无响应）。
    /// </summary>
    static bool ForceForeground(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd)) return true;

        var foreground = GetForegroundWindow();
        uint current = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;

        bool attachedFg = false, attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != current)
                attachedFg = AttachThreadInput(current, foregroundThread, true);
            if (targetThread != 0 && targetThread != current && targetThread != foregroundThread)
                attachedTarget = AttachThreadInput(current, targetThread, true);

            BringWindowToTop(hwnd);
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            if (SetForegroundWindow(hwnd)) return true;
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(current, targetThread, false);
            if (attachedFg) AttachThreadInput(current, foregroundThread, false);
        }

        // 最后一招：模拟一次 ALT 抬起，解除前台锁后再切
        keybd_event(0x12 /* VK_MENU */, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SwitchToThisWindow(hwnd, true);
        if (GetForegroundWindow() == hwnd) return true;

        Log.Info($"Activate: could not bring hwnd {hwnd} to the front (error {Marshal.GetLastWin32Error()})");
        return false;
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
        else
        {
            // 不要一律甩锅给 UIPI：先看是不是真的权限更高（打不开进程句柄），否则就是窗口自己不肯显示。
            string reason = "the window refused to show (ShowWindow had no effect)";
            try
            {
                using var probe = System.Diagnostics.Process.GetProcessById((int)pid);
                _ = probe.Handle;   // 能拿到句柄 = 不是 UIPI 拦截
            }
            catch (Exception)
            {
                reason = "the process runs at a higher integrity level (UIPI blocks us)";
            }
            Log.Info($"Cannot surface pid {pid}: {reason}");
        }
        return true;
    }


    /// <summary>Asks the window to close, exactly like its close button.</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, SC_CLOSE, IntPtr.Zero);
}
