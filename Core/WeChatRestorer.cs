using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MacDock.Core;

/// <summary>
/// 微信（Qt 版）的托盘图标点一下经常"没反应"：它自己处理托盘消息时，
/// 如果主窗口处于隐藏状态，某些情况下不会恢复。这里给一个确定行为：
/// 点托盘图标 → 把微信主窗口显示出来并切到前台。
/// 只对类名以 QWindowIcon 结尾、标题为 微信/Weixin 的窗口动手，不碰别的进程。
/// </summary>
internal static class WeChatRestorer
{
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    const int SW_SHOW = 5;
    const int SW_RESTORE = 9;
    const int SW_SHOWNOACTIVATE = 4;

    public static bool LooksLikeWeChat(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return title!.Contains("微信", StringComparison.Ordinal)
            || title.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Weixin", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>找到微信主窗口并显示到前台；返回是否成功。</summary>
    public static bool Restore()
    {
        var pids = new HashSet<uint>();
        foreach (var name in new[] { "Weixin", "WeChat" })
        {
            foreach (var process in Process.GetProcessesByName(name)) pids.Add((uint)process.Id);
        }
        if (pids.Count == 0) { Log.Info("WeChat: process not running"); return false; }

        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!pids.Contains(pid)) return true;

            var cls = new StringBuilder(128);
            GetClassNameW(hwnd, cls, 128);
            if (cls.ToString().IndexOf("QWindowIcon", StringComparison.OrdinalIgnoreCase) < 0) return true;

            var title = new StringBuilder(160);
            GetWindowTextW(hwnd, title, 160);
            string text = title.ToString();
            if (!(text.Contains("微信") || text.Equals("Weixin", StringComparison.OrdinalIgnoreCase))) return true;

            RECT r;
            GetWindowRect(hwnd, out r);
            int area = Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);

        if (best == IntPtr.Zero) { Log.Info("WeChat: main window not found"); return false; }

        if (IsIconic(best)) ShowWindow(best, SW_RESTORE);
        ShowWindow(best, SW_SHOW);
        SetForegroundWindow(best);
        Log.Info($"WeChat: restored main window (area={bestArea})");
        return true;
    }
}
