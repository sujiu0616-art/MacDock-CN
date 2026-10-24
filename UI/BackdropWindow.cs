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
    readonly Border _tint;
    readonly Image _surface;
    bool _visible = true;

    public BackdropWindow() : base(rounded: false)
    {
        Title = "MacDock Backdrop";
        Content = WallpaperFrost.Build(out _surface, out _tint, 14);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // The plate is cut to a region, which costs the window DWM's system backdrop. The OS blur that
        // replaced it reads almost opaque here, so the plate paints the wallpaper itself instead; the
        // accent blur stays as the fallback for when Windows has no wallpaper to crop.
        UseAccentBlur();
        RefreshSurface(GlassBounds);
    }

    protected override void OnBoundsChanged(RECT bounds) => RefreshSurface(bounds);

    void RefreshSurface(RECT bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var display = Displays.FromPoint(new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 });
        var brush = WallpaperFrost.BrushFor(bounds, display, out double luminance);
        _surface.Source = brush?.ImageSource;
        // No wallpaper to crop (a theme with a solid colour): fall back to the accent blur's own look.
        _surface.Visibility = brush is null ? Visibility.Collapsed : Visibility.Visible;
        _tint.Background = brush is null
            ? new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x59, 0, 0, 0) : Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x2E, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    public override void ApplyTheme()
    {
        // The rim is drawn by the dock's own layer, where it can be a translucent 1px line; DWM's
        // border colour has no alpha, so it would read as a hard grey edge.
        ApplyGlassTheme(DWMWA_COLOR_NONE);
        // The tint is applied by RefreshSurface, which also knows whether there is a wallpaper to show.
    }

    public void SetVisible(bool visible)
    {
        if (Handle == IntPtr.Zero || visible == _visible) return;
        _visible = visible;
        ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }
}
