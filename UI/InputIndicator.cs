using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MacDock.Core;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 菜单栏上的输入法指示器（"中"/"英"）。
///
/// 之前是"假"的：只读 ImmGetContext(前台窗口)。现代程序（Chrome/UWP/Electron）根本不提供 IMM 上下文，
/// 于是永远走到"HKL 是中文布局"这条退路 —— 不管你实际是中文还是英文，界面一直显示"中"。
///
/// 现在的读法（实测有效）：ImmGetDefaultIMEWnd(前台窗口) 拿到该线程的 IME 窗口，
/// 再 WM_IME_CONTROL 问两件事：
///   IMC_GETOPENSTATUS     —— IME 是否打开
///   IMC_GETCONVERSIONMODE —— 转换模式里 IME_CMODE_NATIVE 位 = 中文
/// 这两个值就是 Windows 自己的语言栏用来显示 中/英 的依据。
/// 切换同理：IMC_SETCONVERSIONMODE 反转 NATIVE 位；拿不到 IME 窗口再退回 ImmSimulateHotKey。
/// </summary>
internal static class InputIndicator
{
    const uint IME_CMODE_NATIVE = 0x0001;
    const int WM_IME_CONTROL = 0x0283;
    const int IMC_GETCONVERSIONMODE = 0x0001;
    const int IMC_SETCONVERSIONMODE = 0x0002;
    const int IMC_GETOPENSTATUS = 0x0005;

    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")]
    static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll")]
    static extern bool ImmGetConversionStatus(IntPtr context, out uint conversion, out uint sentence);
    [DllImport("imm32.dll")]
    static extern bool ImmSetConversionStatus(IntPtr context, uint conversion, uint sentence);
    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
    [DllImport("imm32.dll")]
    static extern bool ImmSimulateHotKey(IntPtr hwnd, int hotkeyId);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    // 跨进程给 IME 窗口发消息，用带超时的版本：IME 卡住时最多等 40ms，不拖 UI
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint SMTO_BLOCK = 0x0001;

    /// <summary>带超时读 WM_IME_CONTROL；失败返回 null（调用方沿用上一次的值）。</summary>
    static int? ImeQuery(IntPtr ime, int command)
    {
        IntPtr result;
        IntPtr ok = SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero,
            SMTO_ABORTIFHUNG | SMTO_BLOCK, 40, out result);
        return ok == IntPtr.Zero ? null : (int)result.ToInt64();
    }

    // 上一次成功读到的标签：IME 一时没响应时先显示它，避免"闪一下变成别的"
    static string _last = "";
    [DllImport("user32.dll")]
    static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")]
    static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetLocaleInfoW(int locale, int type, StringBuilder buffer, int size);

    const int LOCALE_SLOCALIZEDDISPLAYNAME = 0x00000002;
    const int IME_HOTKEY_DSWITCH = 0x10;

    /// <summary>"中" 或 "英"：取自前台窗口所属线程的真实 IME 状态。</summary>
    public static string CurrentLabel()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            // 1) 首选：默认 IME 窗口（现代程序也有，实测 Chrome 可用）
            IntPtr ime = ImmGetDefaultIMEWnd(foreground);
            if (ime != IntPtr.Zero)
            {
                int? open = ImeQuery(ime, IMC_GETOPENSTATUS);
                int? conversion = ImeQuery(ime, IMC_GETCONVERSIONMODE);
                if (open is null || conversion is null) return _last.Length > 0 ? _last : "—";
                string label = open != 0 && (conversion.Value & IME_CMODE_NATIVE) != 0 ? "中" : "英";
                _last = label;
                return label;
            }

            // 2) 老式 IMM 程序
            IntPtr context = ImmGetContext(foreground);
            if (context != IntPtr.Zero)
            {
                try
                {
                    if (ImmGetConversionStatus(context, out uint conversion, out _))
                        return (conversion & IME_CMODE_NATIVE) != 0 ? "中" : "英";
                }
                finally { ImmReleaseContext(foreground, context); }
            }
        }
        // 3) 连 IME 都没有（纯英文布局）：按布局判断
        return IsChineseLayout(foreground) ? "中" : "英";
    }

    /// <summary>切换中/英（等价于按 Shift）。</summary>
    public static bool Toggle()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        IntPtr ime = ImmGetDefaultIMEWnd(foreground);
        if (ime != IntPtr.Zero)
        {
            int conversion = (int)SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero).ToInt64();
            int next = conversion ^ (int)IME_CMODE_NATIVE;
            SendMessageTimeout(ime, WM_IME_CONTROL, (IntPtr)IMC_SETCONVERSIONMODE, (IntPtr)next,
                SMTO_ABORTIFHUNG, 60, out _);
            _last = (next & IME_CMODE_NATIVE) != 0 ? "中" : "英";

            // 少数 IME 不认 SETCONVERSIONMODE：再用系统热键模拟一次
            int after = (int)SendMessage(ime, WM_IME_CONTROL, (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero).ToInt64();
            if ((after & IME_CMODE_NATIVE) == (conversion & IME_CMODE_NATIVE))
                return ImmSimulateHotKey(foreground, IME_HOTKEY_DSWITCH);
            return true;
        }

        IntPtr context = ImmGetContext(foreground);
        if (context != IntPtr.Zero)
        {
            try
            {
                if (!ImmGetConversionStatus(context, out uint conversion, out uint sentence)) return false;
                return ImmSetConversionStatus(context, conversion ^ IME_CMODE_NATIVE, sentence);
            }
            finally { ImmReleaseContext(foreground, context); }
        }
        return ImmSimulateHotKey(foreground, IME_HOTKEY_DSWITCH);
    }

    /// <summary>已安装的输入法：(HKL 值, 显示名)。</summary>
    public static List<(IntPtr Layout, string Name)> Installed()
    {
        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];
        var list = new IntPtr[count];
        count = GetKeyboardLayoutList(count, list);
        var result = new List<(IntPtr, string)>(count);
        for (int i = 0; i < count; i++)
        {
            long hkl = list[i].ToInt64();
            int langId = (int)(hkl & 0xFFFF);
            var name = new StringBuilder(128);
            string display = GetLocaleInfoW(langId, LOCALE_SLOCALIZEDDISPLAYNAME, name, name.Capacity) > 0
                ? name.ToString()
                : $"0x{langId:X4}";
            result.Add((list[i], display));
        }
        return result;
    }

    /// <summary>切到指定输入法（和 Win+Space 一样的路径）。</summary>
    public static void Switch(IntPtr layout)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return;
        PostMessage(foreground, 0x0050 /* WM_INPUTLANGCHANGEREQUEST */, IntPtr.Zero, layout);
    }

    static bool IsChineseLayout(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        uint thread = GetWindowThreadProcessId(window, out _);
        long hkl = GetKeyboardLayout(thread).ToInt64();
        return (hkl & 0xFFFF) == 0x0804;   // zh-CN
    }

    /// <summary>给这个窗口装上"点一下切中英、右键选输入法"的行为。</summary>
    public static void Attach(Window owner, FrameworkElement cell)
    {
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Toggle();
        };
        cell.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new DockMenu();
            foreach (var (layout, name) in Installed())
            {
                var target = layout;
                menu.Add(name, () => Switch(target));
            }
            menu.Show(owner, owner.Handle(), GetForegroundWindow());
        };
    }
}
