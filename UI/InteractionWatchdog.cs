using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 交互探针：每 2 秒检查一次"前台窗口现在能不能收到点击、能不能应答消息"。
/// 只在出问题时写日志（平时零输出），用来抓住"窗口没反应"的确凿现场：
///   · 前台窗口的消息泵超过 200ms 不应答 → `Interaction: <进程> 无响应（msgPump>200ms）`
///   · 前台窗口中心点被**别的窗口**接走点击 → `Interaction: 点击被 <进程>/<类名> 抢走`
/// 这样不用用户手动跑脚本，事后再看 log.txt 就能定位。
/// </summary>
internal static class InteractionWatchdog
{
    const int ProbeIntervalMs = 2000;
    const uint WM_NULL = 0x0000;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static Dispatcher? _dispatcher;
    static long _lastReport;

    public static void Start(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProbeIntervalMs) };
        timer.Tick += (_, _) => Probe();
        timer.Start();
    }

    static void Probe()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return;
            if (!GetWindowRect(foreground, out var rect)) return;
            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == Environment.ProcessId) return;   // 我们自己的窗口不算

            string process = ProcessName(pid);
            string className = ClassName(foreground);
            string title = WindowTitle(foreground);

            // 1) 消息泵还活着吗
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var answered = SendMessageTimeout(foreground, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 250, out _);
            watch.Stop();

            if (answered == IntPtr.Zero && watch.ElapsedMilliseconds >= 200)
            {
                Report($"Interaction: {process} 无响应（msgPump>{watch.ElapsedMilliseconds}ms, class={className}, title='{title}'）");
                return;
            }

            // 2) 前台窗口中心点的点击会被谁接走
            var center = new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 };
            var hit = WindowFromPoint(center);
            if (hit == IntPtr.Zero) return;
            var hitRoot = GetAncestor(hit, 2 /* GA_ROOT */);
            if (hitRoot == foreground) return;   // 正常

            GetWindowThreadProcessId(hit, out uint hitPid);
            if (hitPid == Environment.ProcessId) return;
            string hitProcess = ProcessName(hitPid);
            string hitClass = ClassName(hit);
            Report($"Interaction: 点击被 {hitProcess}/{hitClass} 抢走（前台是 {process}, class={className}, title='{title}'）");
        }
        catch (Exception)
        {
            // 探针不许抛
        }
    }

    static void Report(string line)
    {
        long now = Environment.TickCount64;
        if (now - _lastReport < 10000) return;   // 同一类问题 10 秒内只记一次
        _lastReport = now;
        Log.Info(line);
    }

    static string ProcessName(uint pid)
    {
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { return "?"; }
    }

    static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(160);
        _ = GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(160);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
