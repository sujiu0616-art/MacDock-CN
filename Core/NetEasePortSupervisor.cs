using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace MacDock.Core;

/// <summary>
/// 保证"调试端口"这件事有兜底：
/// 红心状态只能从网易云自己的页面读（CEF 调试端口），而端口必须在启动时带上。
/// 这里做三件事：
///   1) 定期检查端口是否可达；
///   2) 若播放器在跑但没端口，等到**没在播放**的时刻（暂停/停止）才优雅重启成带端口的形态，
///      绝不打断正在播的歌；
///   3) 重启用 WM_CLOSE + 原路径带参数启动，不再用杀进程（那会把托盘图标变成死图标）。
/// </summary>
internal static class NetEasePortSupervisor
{
    const int Port = 9222;
    static readonly HttpClient Probe = new(new HttpClientHandler { UseProxy = false, Proxy = null })
    { Timeout = TimeSpan.FromSeconds(1) };

    static long _lastCheckMs;
    static long _lastRestartMs;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    const uint WM_CLOSE = 0x0010;

    public static bool PortOpen()
    {
        try
        {
            using var response = Probe.GetAsync($"http://127.0.0.1:{Port}/json/version").GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>由 MediaWatcher 的秒表驱动；playing 为真时绝不重启。</summary>
    public static void Tick(bool playing)
    {
        long now = Environment.TickCount64;
        if (now - _lastCheckMs < 20_000) return;
        _lastCheckMs = now;

        if (PortOpen()) return;

        var processes = Process.GetProcessesByName("cloudmusic");
        if (processes.Length == 0) return;
        if (playing) return;                                 // 正在播：不动
        if (now - _lastRestartMs < 120_000) return;          // 两分钟内只试一次

        string? exe = null;
        foreach (var process in processes)
        {
            try { exe ??= process.MainModule?.FileName; } catch (Exception) { }
        }
        if (string.IsNullOrEmpty(exe)) return;

        _lastRestartMs = now;
        Log.Info("Port: player runs without debug port - restarting it while idle (no music interrupted)");
        CloseMainWindows(processes);
        Task.Delay(2500).ContinueWith(_ =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe!, $"--remote-debugging-port={Port}") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Info("Port: relaunch failed - " + ex.Message); }
        });
    }

    static void CloseMainWindows(Process[] processes)
    {
        var pids = new HashSet<uint>();
        foreach (var process in processes) pids.Add((uint)process.Id);

        EnumWindows((hwnd, _) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!pids.Contains(pid)) return true;
            var cls = new StringBuilder(64);
            GetClassNameW(hwnd, cls, 64);
            if (cls.ToString() != "OrpheusBrowserHost") return true;
            IntPtr result;
            SendMessageTimeout(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, 2, 3000, out result);
            return false;
        }, IntPtr.Zero);
    }
}
