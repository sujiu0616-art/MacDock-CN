using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using MacDock.Core;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 菜单栏上的输入法指示器（"中"/"英"）。
///
/// 为什么需要它：我们把 Windows 任务栏隐藏了，自带的语言栏/输入指示器没有地方显示，
/// 用户没法判断当前是中文还是英文输入，也没法点一下切换。
///
/// 做法（全部是系统公开 API，不装钩子）：
/// · 读状态：ImmGetContext(前台窗口) + ImmGetConversionStatus → IME_CMODE_NATIVE 位 = 中文
/// · 切换：ImmSetConversionStatus 反转 IME_CMODE_NATIVE
/// · 换输入法：PostMessage(前台窗口, WM_INPUTLANGCHANGEREQUEST, 0, HKL) —— 和 Win+Space 走同一条路
/// </summary>
internal static class InputIndicator
{
    const uint IME_CMODE_NATIVE = 0x0001;
    const int WM_INPUTLANGCHANGEREQUEST = 0x0050;

    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport("imm32.dll")]
    static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);
    [DllImport("imm32.dll")]
    static extern bool ImmGetConversionStatus(IntPtr context, out uint conversion, out uint sentence);
    [DllImport("imm32.dll")]
    static extern bool ImmSetConversionStatus(IntPtr context, uint conversion, uint sentence);

    [DllImport("user32.dll")]
    static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")]
    static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[]? list);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetLocaleInfoW(int locale, int type, StringBuilder buffer, int size);

    const int LOCALE_SLOCALIZEDDISPLAYNAME = 0x00000002;

    /// <summary>"中" 或 "英"：按前台窗口的 IME 转换模式判断；拿不到上下文时退回键盘布局判断。</summary>
    public static string CurrentLabel()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
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
        // 退路：看前台线程的键盘布局是不是中文（0804）
        return IsChineseLayout(foreground) ? "中" : "英";
    }

    /// <summary>切换中/英（等价于按 Shift）。</summary>
    public static bool Toggle()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        IntPtr context = ImmGetContext(foreground);
        if (context == IntPtr.Zero) return false;
        try
        {
            if (!ImmGetConversionStatus(context, out uint conversion, out uint sentence)) return false;
            conversion ^= IME_CMODE_NATIVE;
            return ImmSetConversionStatus(context, conversion, sentence);
        }
        finally { ImmReleaseContext(foreground, context); }
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
        PostMessage(foreground, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, layout);
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
