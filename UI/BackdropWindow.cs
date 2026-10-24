using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// The frosted-glass plate behind the icons. It is a separate, non-layered window because only
/// those get the real Windows 11 acrylic backdrop. It is resized every frame while the dock
/// magnifies. Its outline is cut to a rounded region (<see cref="AcrylicWindow.SetGlassRadius"/>)
/// because DWM's own corner radius is fixed and much tighter than the Dock's.
/// </summary>
internal sealed class BackdropWindow : AcrylicWindow
{
    readonly Border _tint = new();
    bool _visible = true;

    public BackdropWindow() : base(rounded: false)
    {
        Title = "MacDock Backdrop";
        Content = _tint;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The plate is cut to a region, which costs the window DWM's system backdrop: ask for the
        // accent acrylic instead, so the rounded plate still blurs the desktop behind it.
        UseAccentBlur();
    }

    public override void ApplyTheme()
    {
        // The rim is drawn by the dock's own layer, where it can be a translucent 1px line; DWM's
        // border colour has no alpha, so it would read as a hard grey edge.
        ApplyGlassTheme(DWMWA_COLOR_NONE);
        // Light black, light white: the blur brightens whatever is behind the plate, so the tint is what
        // keeps the dark Dock a dark glass (a 20% tint over a bright wallpaper washed out to a pale blue)
        // while still letting the desktop show through, which is the point of the glass.
        _tint.Background = new SolidColorBrush(Theme.IsDark
            ? Color.FromArgb(0x59, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    }

    public void SetVisible(bool visible)
    {
        if (Handle == IntPtr.Zero || visible == _visible) return;
        _visible = visible;
        ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }
}
