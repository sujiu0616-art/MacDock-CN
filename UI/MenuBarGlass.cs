// 菜单栏"完全不透明"：这台 Win11（26300）上 accent 模糊（ACCENT_ENABLE_BLURBEHIND）已经退化成
// 一块不透明深色 —— 实测亮度 47/255，和壁纸 226 差得离谱。
//
// 改成和 Dock 板同一套可靠做法：分层窗口（per-pixel alpha）+ 一层半透明 tint。
// 代价是没有磨砂（不模糊），好处是**真透明**：背后是壁纸、窗口还是视频都直接透出来，
// 而且分层窗口的文字抗锯齿、圆角、发丝线都正常。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 菜单栏底下那条玻璃：分层窗口 + 半透明 tint + 1px 下边框。
/// 不用 OS 的模糊材质，因为这台机器上它会渲染成不透明深色（实测亮度 47/255）。
/// </summary>
internal sealed class MenuBarGlass : Window
{
    readonly Border _tint = new();
    bool _visible = true;

    public MenuBarGlass()
    {
        Title = "MacDock Menu Bar Glass";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        _tint.BorderThickness = new Thickness(0, 0, 0, 1); // macOS 的菜单栏靠一条发丝线和窗口分开
        Content = _tint;
        ApplyTheme();
    }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        MakeToolWindow(Handle);
        // 同上：分层窗口不要 DWM 的圆角，否则左上角会出现一块黑。
        int square = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        // 去掉标题栏系统菜单位：否则无边框窗口的左上角会被画成一块 21×21 的黑方块。
        long style = (long)GetWindowLongPtr(Handle, GWL_STYLE);
        SetWindowLongPtr(Handle, GWL_STYLE, (IntPtr)(style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));
    }

    /// <summary>Moves the strip, in physical screen pixels.</summary>
    public void SetBounds(RECT r)
    {
        if (Handle == IntPtr.Zero || r.Width <= 0 || r.Height <= 0) return;
        SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
    }

    public void ApplyTheme()
    {
        // 真透明：30% 左右的黑（浅色主题 40% 白）+ 一条更深的底边
        _tint.Background = new SolidColorBrush(Theme.IsDark
            ? Color.FromArgb(0x4D, 0x08, 0x08, 0x0C)
            : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        _tint.BorderBrush = new SolidColorBrush(Theme.IsDark
            ? Color.FromArgb(0x33, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
    }

    public void SetVisible(bool visible)
    {
        if (Handle == IntPtr.Zero || visible == _visible) return;
        _visible = visible;
        ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    /// <summary>Kept for the "back from sleep" path; a layered window has no frame to refresh.</summary>
    public void Reactivate() { }
}
