using System.Runtime.InteropServices;
using System.Windows.Threading;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 键盘钩子：把某个"特殊键"接到启动台，并提供一个 --keylog 诊断模式用来查那个键的虚拟码。
///
/// 用法：
/// · 诊断：`MacDock.exe --keylog` 启动后按一下那个键，log.txt 里会出现 `Key: vk=0x... scan=... flags=...`；
/// · 绑定：把 key 的 vk 值写进配置 `LaunchpadKey`（十进制或 0x 十六进制都行），之后按它就打开启动台；
///   设成 0 表示不绑定（默认）。
/// 不碰 Win 键：Win 的组合键（Win+E/D/L）在系统层处理，劫持它只会带来一堆问题。
/// </summary>
internal static class KeyLauncher
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
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

    static IntPtr _hook;
    static HookProc? _proc;
    static bool _keyLog;
    static int _boundKey;
    static DateTime _lastOpen = DateTime.MinValue;

    public static void Install(int boundKey, bool keyLog)
    {
        _boundKey = boundKey;
        _keyLog = keyLog;
        if (_hook != IntPtr.Zero) return;
        _proc ??= Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Info("KeyLauncher: the keyboard hook was refused");
        else if (keyLog) Log.Info("KeyLauncher: key log is on - press the key you want, then look at log.txt");
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                int message = (int)wParam;
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
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
                        // 按住不放时不要疯狂开关：1 秒内只响应一次
                        if ((DateTime.UtcNow - _lastOpen).TotalMilliseconds > 220)
                        {
                            _lastOpen = DateTime.UtcNow;
                            OpenLaunchpad();
                        }
                    }
                }
            }
        }
        catch (Exception) { }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static void OpenLaunchpad()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
        {
            try
            {
                if (LaunchpadWindow.IsOpen) LaunchpadWindow.CloseIfOpen();
                else LaunchpadWindow.Open();
            }
            catch (Exception ex) { Log.Error(ex, "Opening the Launchpad from a key"); }
        }), DispatcherPriority.Input);
    }
}
