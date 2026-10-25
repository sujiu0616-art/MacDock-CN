using System.Runtime.InteropServices;
using System.Text.Json;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Core;

/// <summary>
/// Hides and restores the Windows taskbars. Shared by the dock and the guard process, so it never
/// touches WPF. While the taskbar is hidden a marker file records the user's own taskbar state and
/// the process that hid it: whoever finds the marker after that process is gone puts things back.
/// </summary>
internal static class TaskbarState
{
    const string PrimaryClass = "Shell_TrayWnd";
    const string SecondaryClass = "Shell_SecondaryTrayWnd";

    sealed record Marker(int OriginalState, int OwnerPid, RECT? OriginalRect = null, bool Parked = false, int ParkOwnerPid = 0);

    static string MarkerPath => Path.Combine(DockConfig.Folder, "taskbar.json");

    public static bool IsTaskbarWindow(IntPtr hwnd) => GetClassName(hwnd) is PrimaryClass or SecondaryClass;

    /// <summary>The Explorer process that owns the taskbar, or 0 while there is none.</summary>
    public static uint ExplorerProcessId()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(tray, out uint pid);
        return pid;
    }

    /// <summary>
    /// Explorer's own taskbar. The menu bar's tray service creates a <c>Shell_TrayWnd</c> too, kept on
    /// top so that apps find it first, so a plain <c>FindWindow</c> is not enough.
    /// </summary>
    public static IntPtr FindExplorerTray()
    {
        // Asked every second while the taskbar is hidden: reuse the last answer while that window lives.
        if (_lastTray is { } last && IsWindow(last.Hwnd) && GetClassName(last.Hwnd) == PrimaryClass
            && GetWindowThreadProcessId(last.Hwnd, out uint lastPid) != 0 && lastPid == last.Pid)
            return last.Hwnd;

        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, PrimaryClass, null)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId) continue; // the menu bar's tray service
            if (IsExplorer(pid))
            {
                _lastTray = new FoundTray(hwnd, pid);
                return hwnd;
            }
        }
        _lastTray = null;
        return IntPtr.Zero;
    }

    sealed record FoundTray(IntPtr Hwnd, uint Pid);
    static volatile FoundTray? _lastTray;

    static bool IsExplorer(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Hides every taskbar and has Explorer release its screen space, by switching it to auto-hide
    /// (the only way Explorer stops reserving it). Returns false while there is no taskbar yet.
    /// </summary>
    public static bool Hide()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return false;

        using (Lock())
        {
            var marker = ReadMarker();
            int pid = Environment.ProcessId;
            if (marker is null || marker.OwnerPid != pid)
            {
                // A marker left by a run that could not clean up still holds the user's real state.
                int original = marker?.OriginalState ?? GetState(tray);
                WriteMarker(new Marker(original, pid));
                AutoStart.SetRestoreFallback(true);
                Log.Info($"Taskbar hidden (original state {original})");
            }
            if ((GetState(tray) & ABS_AUTOHIDE) == 0) SetState(tray, ABS_AUTOHIDE | ABS_ALWAYSONTOP);
            // 同时写进注册表：这样开机时 Explorer 一启动就是"自动隐藏"状态，
            // Windows 任务栏根本不会先露出来再被我们藏掉（原来只改运行时状态，重启后它又会亮一下）。
            PersistAutoHide(true);
        }
        HideWindows(tray);
        return true;
    }

    /// <summary>
    /// 兼容模式：任务栏**窗口不隐藏**，只把它设成"自动隐藏"并写进注册表。
    /// 这样 ITaskbarList3（进度/角标）、Toast、FindWindow("Shell_TrayWnd") 这些依赖真实任务栏的
    /// 程序全部照常工作；平时它看不见，鼠标蹭到屏幕底边才滑出来。
    /// </summary>
    public static bool AutoHideOnly()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return false;
        using (Lock())
        {
            var marker = ReadMarker();
            int pid = Environment.ProcessId;
            if (marker is null || marker.OwnerPid != pid)
            {
                int original = marker?.OriginalState ?? GetState(tray);
                WriteMarker(new Marker(original, pid));
                AutoStart.SetRestoreFallback(true);
                Log.Info($"Taskbar auto-hide (original state {original})");
            }
            if ((GetState(tray) & ABS_AUTOHIDE) == 0) SetState(tray, ABS_AUTOHIDE | ABS_ALWAYSONTOP);
            PersistAutoHide(true);
        }
        // 关键区别：这里不调用 HideWindows()，任务栏窗口保持可见（由系统自己藏）
        return true;
    }

    // ── 把任务栏"停到屏幕外"（ABM_SETPOS）────────────────────────────────
    // 停车的意义：任务栏窗口继续存在（依赖它的程序全部照常），但它被移到屏幕外，
    // 既不会冒出来跟 Dock 抢位置，也不需要靠"鼠标贴底边才滑出"那种自动隐藏。

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    const uint ABM_GETTASKBARPOS = 0x00000005;
    const uint ABM_SETPOS = 0x00000003;

    /// <summary>把 Explorer 的任务栏停到屏幕外（保留窗口，只是移走）。</summary>
    static bool IsParkedMarker() => ReadMarker() is { Parked: true };

    public static bool ParkOffscreen()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return false;

        using (Lock())
        {
            var marker = ReadMarker();
            int pid = Environment.ProcessId;
            if (marker is null || marker.OwnerPid != pid || !marker.Parked)
            {
                var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
                SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
                var original = data.rc;
                int state = GetState(tray);
                WriteMarker(new Marker(state, pid, original, Parked: true, ParkOwnerPid: pid));
                AutoStart.SetRestoreFallback(true);
                Log.Info($"Taskbar parked offscreen (original rect {original.Left},{original.Top} {original.Width}x{original.Height})");
            }
            // 先确保不会被自动隐藏"救"回画面里
            if ((GetState(tray) & ABS_AUTOHIDE) != 0) SetState(tray, ABS_ALWAYSONTOP);
            PersistAutoHide(false);
            MoveAway(tray);
        }
        return true;
    }

    /// <summary>把任务栏挪回原位置（hotkey / 退出 / --restore-taskbar 都走这里）。</summary>
    public static bool Unpark()
    {
        var marker = ReadMarker();
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return false;

        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        var target = marker?.OriginalRect ?? data.rc;

        SHAppBarMessage(ABM_SETPOS, ref data);
        data.rc = target;
        SHAppBarMessage(ABM_SETPOS, ref data);
        SetWindowPos(tray, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Log.Info($"Taskbar unparked to ({target.Left},{target.Top}) {target.Width}x{target.Height}");
        return true;
    }

    /// <summary>当前是否处于"停在屏幕外"的状态。</summary>
    public static bool IsParked()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return false;
        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        return data.rc.Top >= ScreenBottom() - 4 || data.rc.Left >= ScreenRight() - 4;
    }

    static int ScreenBottom()
    {
        var display = Displays.ByNameOrPrimary(null);
        return display.Bounds.Bottom;
    }

    static int ScreenRight()
    {
        var display = Displays.ByNameOrPrimary(null);
        return display.Bounds.Right;
    }

    static void MoveAway(IntPtr tray)
    {
        var data = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        SHAppBarMessage(ABM_GETTASKBARPOS, ref data);
        int bottom = ScreenBottom(), right = ScreenRight();
        // 停到屏幕下方 4000px：系统认为它还在（appbar 仍注册），但屏幕上完全看不到
        var offscreen = new RECT { Left = 0, Top = bottom + 4000, Right = data.rc.Width, Bottom = bottom + 4000 + data.rc.Height };
        data.rc = offscreen;
        SHAppBarMessage(ABM_SETPOS, ref data);
        SetWindowPos(tray, IntPtr.Zero, offscreen.Left, offscreen.Top, offscreen.Width, offscreen.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>Cheap re-check while hidden: hides any taskbar Windows has shown again.</summary>
    public static void Enforce()
    {
        var tray = FindExplorerTray();
        if (tray == IntPtr.Zero) return;
        // Someone (the user, Settings, a guard that saw a stale owner) turned auto-hide off.
        if (IsParkedMarker()) { MoveAway(tray); return; }
        if ((GetState(tray) & ABS_AUTOHIDE) == 0) Hide();
        else HideWindows(tray); // covers a taskbar Explorer re-created elsewhere
    }

    /// <summary>
    /// Puts the user's taskbar state back and shows every taskbar, if one of our processes hid it.
    /// With <paramref name="onlyForPid"/>, only if that process was the one. <paramref name="force"/>
    /// shows the taskbars even without a marker (emergency command line).
    /// Returns true when something was restored.
    /// </summary>
    public static bool Restore(int? onlyForPid = null, bool force = false)
    {
        using (Lock())
        {
            var marker = ReadMarker();
            if (onlyForPid is int pid && marker?.OwnerPid != pid) return false;
            if (marker is null && !force) return false;

            // No Explorer right now: keep the marker, the next run (or Explorer's own start) sorts it out.
            var tray = FindExplorerTray();
            if (tray == IntPtr.Zero) return false;

            if (marker is not null)
            {
                SetState(tray, marker.OriginalState);
                PersistAutoHide((marker.OriginalState & ABS_AUTOHIDE) != 0);
            }
            if (marker is { Parked: true }) Unpark();
            foreach (var h in ExplorerTaskbarWindows()) ShowWindow(h, SW_SHOWNA);
            DeleteMarker();
            AutoStart.SetRestoreFallback(false);
            Log.Info($"Taskbar restored (state {marker?.OriginalState.ToString() ?? "unchanged"}, process {Environment.ProcessId})");
            return true;
        }
    }

    /// <summary>
    /// The sign-in fallback (see <see cref="AutoStart.SetRestoreFallback"/>): waits for Explorer's
    /// taskbar, then restores it unless a running MacDock is the one keeping it hidden.
    /// </summary>
    public static void RestoreIfOrphaned()
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (FindExplorerTray() == IntPtr.Zero && DateTime.UtcNow < deadline) Thread.Sleep(1000);

        int? owner;
        using (Lock()) owner = ReadMarker()?.OwnerPid;
        if (owner is not int pid) return;
        if (IsRunningDock(pid)) return;
        if (Restore(onlyForPid: pid)) Log.Info("Taskbar restored at sign-in: MacDock was not running");
        else AutoStart.SetRestoreFallback(true); // no Explorer yet: try again at the next sign-in
    }

    static bool IsRunningDock(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, "MacDock", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Explorer keeps the auto-hide state in memory and saves it when it pleases (at the latest when it
    /// exits). Writing it too means a restart or a crash of Explorer right after can never bring back
    /// the auto-hide we set. Byte 8 of <c>StuckRects3\Settings</c>, bit 0 = auto-hide.
    /// </summary>
    static void PersistAutoHide(bool autoHide)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
        try
        {
            using var stuck = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, writable: true);
            if (stuck?.GetValue("Settings") is not byte[] { Length: > 8 } settings) return;
            byte updated = (byte)(autoHide ? settings[8] | 1 : settings[8] & ~1);
            if (updated == settings[8]) return;
            settings[8] = updated;
            stuck.SetValue("Settings", settings, Microsoft.Win32.RegistryValueKind.Binary);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the taskbar state to the registry");
        }
    }

    static void HideWindows(IntPtr tray)
    {
        // Not just `tray`: an Explorer restart can leave a stale hidden Shell_TrayWnd behind next to
        // the live one, and the cached handle may point at either.
        foreach (var h in ExplorerTaskbarWindows())
            if (IsWindowVisible(h)) ShowWindow(h, SW_HIDE);
    }

    /// <summary>Every taskbar window Explorer owns right now (primary and per-monitor), fresh each call.</summary>
    static List<IntPtr> ExplorerTaskbarWindows()
    {
        // 每秒（现在是每 2 秒）都要跑一次：不能为每个窗口开 Process 对象查进程名，只比对 explorer 的 pid。
        uint explorer = ExplorerProcessId();
        var result = new List<IntPtr>();
        foreach (var className in new[] { PrimaryClass, SecondaryClass })
        {
            var hwnd = IntPtr.Zero;
            while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, className, null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == Environment.ProcessId) continue;       // the menu bar's tray service
                if (explorer != 0 && pid == explorer) result.Add(hwnd);
            }
        }
        return result;
    }

    /// <summary>The primary taskbar and one per extra monitor.</summary>
    static List<IntPtr> TaskbarWindows(IntPtr tray)
    {
        // FindWindowEx filters by class itself: far cheaper, every second, than reading every window's class.
        var result = new List<IntPtr> { tray };
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, SecondaryClass, null)) != IntPtr.Zero) result.Add(hwnd);
        return result;
    }

    static int GetState(IntPtr tray)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    static void SetState(IntPtr tray, int state)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = tray, lParam = state };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    static Marker? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath)) : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the taskbar state");
            return null;
        }
    }

    static void WriteMarker(Marker marker)
    {
        try
        {
            Directory.CreateDirectory(DockConfig.Folder);
            var tmp = MarkerPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marker));
            File.Move(tmp, MarkerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the taskbar state");
        }
    }

    static void DeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deleting the taskbar state");
        }
    }

    /// <summary>Serializes marker updates between the dock and the guard.</summary>
    static MutexLock Lock()
    {
        var mutex = new Mutex(false, @"Local\MacDock.TaskbarState");
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            owned = true; // its previous owner died mid-update; the marker file is still whole
        }
        return new MutexLock(mutex, owned);
    }

    readonly struct MutexLock(Mutex mutex, bool owned) : IDisposable
    {
        public void Dispose()
        {
            if (owned) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
