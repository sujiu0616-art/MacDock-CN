using System;
using System.Runtime.InteropServices;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

internal static class WindowActions
{
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    /// <summary>Restores (if minimized) and brings a window to the front.</summary>
    public static void Activate(IntPtr hwnd)
    {
        // 句柄可能是托盘消息窗口 / tooltip / Electron 的内部 Chromium 窗口这类假窗口：
        // 先按进程把真正的主窗口叫回来。叫不回来就继续按原句柄走，别在这里提前收工
        // （早先直接 return，结果"应用有窗口但唤不起来"时什么都不做）。
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
        ConfirmForeground(hwnd);
    }

    /// <summary>
    /// 激活之后**确认**前台真的换了。SetForegroundWindow 返回 true 却没真正交接的情况很常见
    /// （前台锁 + 应用自己又把焦点抢回去），表现就是"窗口弹出来了但点不动，再点一次任务栏才行"。
    /// 这里在 90ms 后复查一次，不对就再抢一遍。
    /// </summary>
    static void ConfirmForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            System.Threading.Thread.Sleep(90);
            try
            {
                if (GetForegroundWindow() == hwnd) return;
                bool ok = ForceForeground(hwnd);
                Log.Info(ok
                    ? $"Activate: foreground re-taken for hwnd {hwnd}"
                    : $"Activate: hwnd {hwnd} still not in front after retry (fg={GetForegroundWindow()})");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Re-taking the foreground");
            }
        });
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
            // SetFocus 是这里的关键：只 SetForegroundWindow 不把键盘焦点交过去，窗口看着在前台，
            // 实际输入还挂在上一个线程上（用户感受就是"窗口点不动"）。
            SetFocus(hwnd);
            if (SetForegroundWindow(hwnd))
            {
                Log.Info($"Activate ok (attach) fg={GetForegroundWindow()}");
                return true;
            }
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(current, targetThread, false);
            if (attachedFg) AttachThreadInput(current, foregroundThread, false);
        }

        // 最后一招只用 SwitchToThisWindow。
        // 注意：**不要再模拟 ALT 按下/抬起** —— 裸发 ALT 会把系统的"任务切换遮罩"
        // （explorer / XamlExplorerHostIslandWindow，标题"任务切换"）唤起来并卡在最前面，
        // 表现就是"窗口能拖、但内容区点击全都没反应"。已实测踩过这个坑。
        SwitchToThisWindow(hwnd, true);
        if (GetForegroundWindow() == hwnd) return true;

        // 真的最后一招：借 Explorer 的激活通道（和点任务栏按钮是同一条路）。
        // 这条路不受前台锁约束 —— 我们自己的 SetForegroundWindow 在"点击被判给 explorer"之后
        // 会时灵时不灵，而那正是"窗口弹出来却点不动、再点一次任务栏才好"的来源。
        if (ActivateViaTaskbar(hwnd))
        {
            System.Threading.Thread.Sleep(40);
            if (GetForegroundWindow() == hwnd) return true;
        }

        Log.Info($"Activate: could not bring hwnd {hwnd} to the front (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    // ── Explorer 的激活通道 ──────────────────────────────────────────────
    // ITaskbarList3::ActivateTab 与"点击任务栏按钮"等价。只声明到 ActivateTab 为止（vtable 前缀必须一致）。
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    class TaskbarList { }

    static ITaskbarList3? _taskbarList;

    static bool ActivateViaTaskbar(IntPtr hwnd)
    {
        try
        {
            _taskbarList ??= (ITaskbarList3)new TaskbarList();
            _taskbarList.HrInit();
            _taskbarList.ActivateTab(hwnd);
            return true;
        }
        catch (Exception ex)
        {
            Log.Info("ActivateTab failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Minimizes through the window's own system command, so the app sees it and animates.</summary>
    public static void Minimize(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, SC_MINIMIZE, IntPtr.Zero);

    /// <summary>
    /// 应用的消息窗口/托盘宿主/输入法窗口不是"主窗口"，但它们往往有标题、面积也不小：
    /// 微信的 <c>WxTrayIconMessageWindow</c> 是 1920×1067，按面积挑必然挑到它，
    /// 于是从 Dock 点微信会弹出一个空白窗口（实测）。
    /// </summary>
    static string TitleOf(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new System.Text.StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    static bool IsInternalWindow(IntPtr hwnd)
    {
        string cls = GetClassName(hwnd);
        string title = TitleOf(hwnd);
        static bool Looks(string s) =>
            s.Contains("MessageWindow", StringComparison.OrdinalIgnoreCase)
            || s.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)
            || s.Contains("TrayIcon", StringComparison.OrdinalIgnoreCase)
            || s.Contains("TrayHost", StringComparison.OrdinalIgnoreCase);
        if (Looks(cls) || Looks(title)) return true;
        return cls is "IME" or "MSCTFIME UI" or "Base_PowerMessageWindow";
    }

    /// <summary>
    /// 在一堆隐藏/最小化的顶层窗口里挑"主窗口"：优先用系统认定的那个（Process.MainWindowHandle），
    /// 其次才在候选里挑非 owned、面积最大的。挑错窗口是"窗口弹出来了但点不动/弹空白窗"的直接原因。
    /// </summary>
    static IntPtr PickMainWindow(uint pid, List<IntPtr> candidates)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var main = process.MainWindowHandle;
            if (main != IntPtr.Zero && candidates.Contains(main)) return main;
        }
        catch (Exception)
        {
            // 进程已退出/读不到：走下面的兜底
        }

        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        foreach (var hwnd in candidates)
        {
            if (IsInternalWindow(hwnd)) continue;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) continue;   // 有 owner：对话框/附属窗口
            if (!GetWindowRect(hwnd, out var rect)) continue;
            long area = (long)rect.Width * rect.Height;
            if (area > bestArea) { bestArea = area; best = hwnd; }
        }
        return best != IntPtr.Zero ? best : candidates[0];
    }

    const uint GW_OWNER = 4;
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    /// <summary>把 <paramref name="executable"/> 对应进程里"藏着"的顶层窗口显示出来并激活（缩到托盘/最小化时隐藏）。
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
            if (IsInternalWindow(hwnd)) return true;              // 消息窗口 / 托盘宿主 / 输入法
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) revealed.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        if (revealed.Count == 0) return false;

        // 只唤起**一个主窗口**。早先这里把所有隐藏窗口全 ShowWindow 一遍：
        // 微信"关闭到托盘"后有 5 个隐藏窗口（主窗 + 若干聊天窗），一次全弹出来之后
        // 前台却被设在 revealed[0]（很可能是聊天窗），用户看到的就是"窗口出来了却点不动"
        // （实测日志：Surfaced 5 window(s) of pid 18612）。
        var target = PickMainWindow(pid, revealed);
        // 必须用**同步** ShowWindow：ShowWindowAsync 投递完就返回，紧接着的可见性检查会读到旧状态，
        // 于是这条路径每次都被判成 "refused to show"（实测 DeepSeek Harness 必现），
        // 而 Activate 看到失败就直接 return，连 ForceForeground 都不走。
        ShowWindow(target, SW_RESTORE);
        if (!IsWindowVisible(target)) ShowWindow(target, 5);   // SW_SHOW
        if (!IsWindowVisible(target))
            SetWindowPos(target, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | 0x0040 /* SWP_SHOWWINDOW */);

        if (IsWindowVisible(target) && !IsIconic(target))
        {
            ForceForeground(target);
            ConfirmForeground(target);
        }

        // 提权进程（UIPI）会无视我们的 ShowWindow：确认一下有没有真的显示出来，把结论写进日志。
        bool shown = IsWindowVisible(target) && !IsIconic(target);
        if (shown)
        {
            Log.Info($"Surfaced hwnd {target} of pid {pid} ({revealed.Count} hidden window(s) seen)");
            return true;
        }

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
        return false;   // 没显示出任何窗口：让调用方继续按原句柄走完整激活
    }


    /// <summary>
    /// 自愈：把卡住不走的"任务切换遮罩"收掉。
    /// 该遮罩是 explorer 的 <c>XamlExplorerHostIslandWindow</c>（标题"任务切换"），
    /// 它一旦卡在最前面，其它应用的**内容区**就收不到点击（窗口框架仍可拖动），
    /// 用户感知就是"这个软件无响应"。用户自己按 Alt+Tab 时它本来就该在，所以只在
    /// 没有按住 Alt、且它已经持续可见一段时间时才动手。
    /// </summary>
    public static void RepairStuckTaskSwitcher(long visibleSinceTicks)
    {
        if (visibleSinceTicks == 0) return;
        if ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0) return;                 // 用户自己在切换
        if (Environment.TickCount64 - visibleSinceTicks < 1500) return;        // 刚出现，可能是正常的
        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, 2, UIntPtr.Zero);
        Log.Info("Repaired a stuck task-switch overlay (content clicks were being swallowed)");
    }

    const byte VK_MENU = 0x12, VK_ESCAPE = 0x1B;
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    /// <summary>Asks the window to close, exactly like its close button.</summary>
    public static void Close(IntPtr hwnd) => PostMessage(hwnd, WM_SYSCOMMAND, SC_CLOSE, IntPtr.Zero);
}
