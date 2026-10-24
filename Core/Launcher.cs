using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Core;

internal static class Launcher
{
    /// <summary>Whether the item can be started elevated: a program, shortcut or script (not a Store app or a folder).</summary>
    public static bool CanRunAsAdmin(DockItem item)
    {
        var target = Environment.ExpandEnvironmentVariables(item.Target);
        return !item.IsStart && !item.IsFolder && !item.IsTrash && (File.Exists(target) || IsWindowsTerminal(item));
    }

    static bool IsWindowsTerminal(DockItem item) =>
        item.Target.Contains("Microsoft.WindowsTerminal", StringComparison.OrdinalIgnoreCase);

    const uint ASFW_ANY = 0xFFFFFFFF;
    const int ASFW_ANY_PID = -1;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AllowSetForegroundWindow(uint processId);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb, lpFile, lpParameters, lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    const uint SEE_MASK_NOASYNC = 0x00000100;
    const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    const int SW_SHOWNORMAL = 1;
    const int ERROR_CANCELLED = 1223;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    /// <summary>
    /// Starts the item with the "Run as administrator" verb. <paramref name="owner"/> must be a window of
    /// ours in front: UAC puts its prompt in front only when asked from the foreground window, otherwise
    /// it waits as a flashing taskbar button — and the taskbar is hidden.
    /// </summary>
    public static void LaunchAsAdmin(DockItem item, IntPtr owner)
    {
        var target = ElevationTarget(item);
        var dir = Path.GetDirectoryName(target);
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            hwnd = owner,
            lpVerb = "runas",
            lpFile = target,
            lpParameters = string.IsNullOrEmpty(item.Config.Arguments) ? null : item.Config.Arguments,
            lpDirectory = File.Exists(target) && !string.IsNullOrEmpty(dir) ? dir : null,
            nShow = SW_SHOWNORMAL,
        };
        if (ShellExecuteEx(ref info)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != ERROR_CANCELLED) // the user said no
            Log.Info($"Launching {item.Name} as administrator ({target}) failed: error {error}");
    }

    /// <summary>
    /// What to elevate. Store apps cannot be elevated through their shell path: Windows Terminal through
    /// its program alias.
    /// </summary>
    static string ElevationTarget(DockItem item)
    {
        if (IsWindowsTerminal(item))
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            return File.Exists(alias) ? alias : "wt.exe";
        }
        return Environment.ExpandEnvironmentVariables(item.Target);
    }

    public static void Launch(DockItem item)
    {
        try
        {
            var target = Environment.ExpandEnvironmentVariables(item.Target);
            // 已经是"进程在、窗口藏着"的状态（缩到托盘的客户端、Task Manager 的「最小化时隐藏」）：
            // 先把隐藏窗口叫回来，别去起第二个实例（单实例程序会立刻自己退出，看着像点了没反应）。
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && WindowActions.SurfaceHiddenWindows(target))
                return;
            // 让新进程有权把自己切到前台：否则它常被前台锁挡在别的窗口后面，看上去像"点了没反应"。
            try { AllowSetForegroundWindow(ASFW_ANY); } catch (Exception) { /* 非关键路径 */ }

            ProcessStartInfo psi;
            var dir = File.Exists(target) ? Path.GetDirectoryName(target) : null;
            if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || ShellItems.IsVirtual(target))
            {
                // Packaged apps and shell folders open reliably through Explorer.
                psi = new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true };
            }
            else if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
            {
                // 普通 exe 直接 CreateProcess：比绕 shell 快，也不会触发 shell 的额外检查。
                psi = new ProcessStartInfo(target)
                {
                    UseShellExecute = false,
                    Arguments = item.Config.Arguments ?? "",
                };
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            else
            {
                // .lnk / .url / 其它：交给 shell 解析（它才知道快捷方式指向谁）。
                psi = new ProcessStartInfo(target) { UseShellExecute = true, Arguments = item.Config.Arguments ?? "" };
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            var started = Process.Start(psi);
            if (started is not null) AfterLaunch(item, target, started);
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Launching {item.Name}");
        }
    }

    /// <summary>
    /// 启动之后收尾：有些客户端（bilibili）直接起 exe 会立刻 exit 0，要求经自己的
    /// resources\elevate.exe 拉起；起来之后主窗口又是隐藏的，要 SC_RESTORE 才显示。
    /// </summary>
    static void AfterLaunch(DockItem item, string target, System.Diagnostics.Process started)
    {
        string dir = Path.GetDirectoryName(target) ?? "";
        string name = item.Name;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500).ConfigureAwait(false);
                bool died = false;
                try { died = started.HasExited; } catch (Exception) { }
                if (died && TryElevationHelper(target, dir))
                {
                    Log.Info($"{name}: started directly then exited; relaunched through the app's own elevate helper");
                    await Task.Delay(2500).ConfigureAwait(false);
                }

                // 把它藏起来（或最小化）的主窗口叫回来
                if (!WindowActions.SurfaceHiddenWindows(target))
                {
                    try
                    {
                        if (started.HasExited && started.ExitCode == 0)
                            Log.Info($"{name}: exited by itself right after start (exit 0)");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Finishing the launch of {name}");
            }
            finally
            {
                try { started.Dispose(); } catch (Exception) { }
            }
        });
    }

    /// <summary>目录里有 resources\elevate.exe 就用它拉起目标（bilibili 这类客户端要求这样做）。</summary>
    static bool TryElevationHelper(string target, string dir)
    {
        try
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string helper = Path.Combine(dir, "resources", "elevate.exe");
            if (!File.Exists(helper)) return false;
            var psi = new ProcessStartInfo(helper)
            {
                UseShellExecute = true,
                WorkingDirectory = dir,
                Arguments = "\"" + target + "\"",
            };
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launching through the app's own elevate helper");
            return false;
        }
    }

}
