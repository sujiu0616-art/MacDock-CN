using System.Runtime.InteropServices;
using System.Windows.Threading;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 键盘钩子：把某个"特殊键"（默认 F4）接到启动台，并保留 --keylog 诊断。
///
/// 关键点：钩子装在**独立后台线程**上，不挂在 UI 线程。
/// 低级键盘钩子（WH_KEYBOARD_LL）的回调是在"安装它的那个线程"里同步执行的；
/// 挂在 UI 线程时，我们的线程一忙（启动台扫描、图标解码、强制 GC…），
/// 全系统的键盘输入都要排队等它 → 别的软件看起来"无响应"，
/// 点一下菜单栏/托盘让我们的线程转起来，积压的输入一放行就"恢复"了。
/// 现在钩子线程只做一件事：匹配按键，然后把动作丢给 UI 线程。
/// </summary>
internal static class KeyLauncher
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint WM_QUIT = 0x0012;

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

    static IntPtr _hook;
    static HookProc? _proc;          // 必须保住委托，否则被 GC 回收后钩子失效
    static Thread? _thread;
    static uint _threadId;
    static bool _keyLog;
    static int _boundKey;
    static long _lastOpenTicks;   // 防连发（Environment.TickCount64）

    public static void Install(int boundKey, bool keyLog)
    {
        _boundKey = boundKey;
        _keyLog = keyLog;
        if (_thread is not null) return;

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
        if (_keyLog) Log.Info("KeyLauncher: key log is on - press the key you want, then look at log.txt");

        // 低级钩子必须由安装线程自己泵消息，否则回调不会被投递
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
            int message = (int)wParam;
            if (code >= 0 && message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (_keyLog)
                    Log.Info($"Key: vk=0x{info.vkCode:X2} ({info.vkCode}) scan=0x{info.scanCode:X2} flags=0x{info.flags:X}");

                bool modifiersHeld = (GetAsyncKeyState(0x12 /* VK_MENU */) & 0x8000) != 0
                                    || (GetAsyncKeyState(0x11 /* VK_CONTROL */) & 0x8000) != 0
                                    || (GetAsyncKeyState(0x10 /* VK_SHIFT */) & 0x8000) != 0
                                    || (GetAsyncKeyState(0x5B /* VK_LWIN */) & 0x8000) != 0;
                if (_boundKey != 0 && info.vkCode == (uint)_boundKey && !modifiersHeld)
                {
                    long now = Environment.TickCount64;
                    if (now - _lastOpenTicks > 220)
                    {
                        _lastOpenTicks = now;
                        ToggleLaunchpad();
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

    /// <summary>把动作丢回 UI 线程执行；调用方（钩子线程）绝不阻塞。</summary>
    static void ToggleLaunchpad()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
        {
            try
            {
                if (LaunchpadWindow.IsOpen) LaunchpadWindow.CloseIfOpen();
                else LaunchpadWindow.Open();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Opening the Launchpad from a key");
            }
        }), DispatcherPriority.Input);
    }

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
