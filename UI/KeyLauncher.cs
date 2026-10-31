using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 键盘钩子：只负责"启动台热键（默认 F4）"与 --keylog 诊断，外加启动台开着时的 Esc 兜底。
///
/// 不再接管 Win 键：Win 完全交回系统，开始菜单用原生的（2026-11-01 用户决定）。
/// 钩子装在独立后台线程上，不占 UI 线程——回调里只做匹配，然后把动作丢回 UI 线程。
/// </summary>
internal static class KeyLauncher
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;
    const uint WM_QUIT = 0x0012;
    const uint LLKHF_INJECTED = 0x10;
    const uint VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x, y;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    static IntPtr _hook;
    static HookProc? _proc;
    static Thread? _thread;
    static uint _threadId;
    static bool _keyLog;
    static int _boundKey;
    static long _lastOpenTicks;

    /// <summary>钩子是否还活着（Windows 在异常情况下会悄悄摘掉钩子）。</summary>
    public static bool HookAlive => _hook != IntPtr.Zero && _thread is not null;

    /// <summary>启动台热键的动作；由 DockWindow 接管（用于判断全屏时不响应）。</summary>
    public static Action? OnLaunchpadTap;

    /// <summary>
    /// 按下 Win 时通知（**不拦截**，原生开始菜单照常弹出）。
    /// DockWindow 用它把菜单栏 + Dock 立刻放出来，配合原生开始菜单一起出现。
    /// </summary>
    public static Action? OnWinDown;

    public static void Install(int boundKey, bool keyLog)
    {
        _boundKey = boundKey;
        _keyLog = keyLog;
        if (_thread is not null && _hook != IntPtr.Zero) return;
        if (_thread is not null && _hook == IntPtr.Zero) _thread = null;   // 钩子丢了：重建线程

        _thread = new Thread(HookLoop)
        {
            IsBackground = true,
            Name = "MacDock key hook",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    static void HookLoop()
    {
        _threadId = (uint)GetCurrentThreadId();
        _proc ??= Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Log.Info("KeyLauncher: the keyboard hook was refused");
            return;
        }
        Log.Info($"KeyLauncher: hook installed (launchpadKey={_boundKey})");
        if (_keyLog) Log.Info("KeyLauncher: key log is on - press the key you want, then look at log.txt");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }

    public static void Uninstall()
    {
        var thread = _thread;
        if (thread is null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
        _threadId = 0;
        _hook = IntPtr.Zero;
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int message = (int)wParam;
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

                if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                {
                    if (_keyLog)
                        Log.Info($"Key: vk=0x{info.vkCode:X2} ({info.vkCode}) scan=0x{info.scanCode:X2} flags=0x{info.flags:X}");

                    // 单按 Win / Win 组合键：都先把外壳（菜单栏 + Dock）放出来。
                    // 只通知，不吞键 —— 开始菜单由系统自己弹，零延迟、零副作用。
                    if (info.vkCode == 0x5B || info.vkCode == 0x5C)
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        dispatcher?.BeginInvoke(new Action(() =>
                        {
                            try { OnWinDown?.Invoke(); }
                            catch (Exception ex) { Log.Error(ex, "Handling a Win key press"); }
                        }), DispatcherPriority.Send);
                    }

                    // 启动台开着时，Esc 由全局钩子兜底：窗口没拿到焦点也能关
                    if (info.vkCode == VK_ESCAPE && LaunchpadWindow.IsOpen)
                    {
                        var d = System.Windows.Application.Current?.Dispatcher;
                        d?.BeginInvoke(new Action(() => LaunchpadWindow.CloseIfOpen()), DispatcherPriority.Input);
                        return (IntPtr)1;
                    }

                    bool modifiersHeld = (GetAsyncKeyState(0x12) & 0x8000) != 0
                                        || (GetAsyncKeyState(0x11) & 0x8000) != 0
                                        || (GetAsyncKeyState(0x10) & 0x8000) != 0
                                        || (GetAsyncKeyState(0x5B) & 0x8000) != 0;
                    if (_boundKey != 0 && info.vkCode == (uint)_boundKey && !modifiersHeld)
                    {
                        long now = Environment.TickCount64;
                        if (now - _lastOpenTicks > 220)
                        {
                            _lastOpenTicks = now;
                            DispatchLaunchpad();
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 钩子回调里绝不抛
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static void DispatchLaunchpad()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
        {
            try
            {
                if (OnLaunchpadTap is not null) OnLaunchpadTap();
                else if (LaunchpadWindow.IsOpen) LaunchpadWindow.CloseIfOpen();
                else LaunchpadWindow.Open();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening the Launchpad from a key");
            }
        }), DispatcherPriority.Input);
    }
}
