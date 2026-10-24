using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// The frosted plate behind the icons.
///
/// It is a <b>layered</b> window (per-pixel alpha) rather than the non-layered one the OS blur needed:
/// a layered window can be clipped to a rounded outline by WPF, so the plate's corners are antialiased.
/// The previous version was cut to a window region (<c>SetWindowRgn</c>), whose edges are hard —
/// under the Dock's 1px rim you could see the stair-steps, which is what made the glass edge look rough.
/// Nothing is lost by going layered: the plate paints its own blurred crop of the wallpaper
/// (<see cref="WallpaperFrost"/>), so it never needed the OS backdrop.
/// </summary>
internal sealed class BackdropWindow : Window
{
    readonly Border _tint;
    readonly Image _surface;
    readonly Grid _layers;
    bool _visible = true;
    int _radiusPx;

    public BackdropWindow()
    {
        Title = "MacDock Backdrop";
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

        _layers = WallpaperFrost.Build(out _surface, out _tint, 14);
        Content = _layers;
        SizeChanged += (_, _) => UpdateClip();
    }

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        MakeToolWindow(Handle);
        UpdateClip();
    }

    /// <summary>Moves the plate, in physical screen pixels.</summary>
    public void SetBounds(RECT r)
    {
        if (Handle == IntPtr.Zero || r.Width <= 0 || r.Height <= 0) return;
        SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
        RefreshSurface(r);
        UpdateClip();
    }

    /// <summary>Corner radius in physical pixels (0 = square). WPF wants device-independent units.</summary>
    public void SetGlassRadius(int pixels)
    {
        if (pixels == _radiusPx) return;
        _radiusPx = Math.Max(0, pixels);
        UpdateClip();
    }

    /// <summary>
    /// Clips the content to a rounded rectangle. WPF clips are antialiased, unlike a window region,
    /// which is the whole point of this window being layered.
    /// </summary>
    void UpdateClip()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double radius = _radiusPx / (scale <= 0 ? 1 : scale);
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        _layers.Clip = radius > 0.5 ? new RectangleGeometry(rect, radius, radius) : null;
    }

    void RefreshSurface(RECT bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var display = Displays.FromPoint(new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 });
        var brush = WallpaperFrost.BrushFor(bounds, display, out _);
        _surface.Source = brush?.ImageSource;
        _surface.Visibility = brush is null ? Visibility.Collapsed : Visibility.Visible;
        // No wallpaper to crop (a theme with a solid colour): a denser tint keeps the icons readable.
        _tint.Background = brush is null
            ? new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x59, 0, 0, 0) : Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Theme.IsDark ? Color.FromArgb(0x2E, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    public void ApplyTheme()
    {
        if (Handle == IntPtr.Zero) return;
        RefreshSurface(new RECT
        {
            Left = (int)Left,
            Top = (int)Top,
            Right = (int)(Left + ActualWidth),
            Bottom = (int)(Top + ActualHeight),
        });
        UpdateClip();
    }

    public void SetVisible(bool visible)
    {
        if (Handle == IntPtr.Zero || visible == _visible) return;
        _visible = visible;
        ShowWindow(Handle, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    }

    /// <summary>Kept for the dock's "come back from sleep" path; a layered window has no frame to refresh.</summary>
    public void Reactivate() { }
}
