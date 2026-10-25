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

        _tint = new Border();
        Content = _tint;
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
        _tint.Clip = radius > 0.5 ? new RectangleGeometry(rect, radius, radius) : null;
    }

    public void ApplyTheme()
    {
        if (Handle == IntPtr.Zero) return;
        // 真透明：只留一层很淡的 tint，背后是壁纸、窗口还是视频都能透出来（不模糊）。
        _tint.Background = new SolidColorBrush(Theme.IsDark
            ? Color.FromArgb(0x38, 0x08, 0x08, 0x0C)   // 22%
            : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)); // 25%
        UpdateClip();
    }

    /// <summary>开机时用：把整块玻璃从不透明到淡入（只在 --autostart 首帧调用一次）。</summary>
    public void FadeIn(int ms)
    {
        _tint.Opacity = 0;
        _tint.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
    }

    /// <summary>
    /// 开机时用：整块玻璃从屏幕底边下方滑上来（fromDiu = 起始下移距离）。
    /// 只动 RenderTransform，不触发布局；日常没有任何动画。
    /// </summary>
    public void SlideIn(int ms, double fromDiu)
    {
        var slide = new TranslateTransform(0, fromDiu);
        _tint.RenderTransform = slide;
        var motion = new System.Windows.Media.Animation.DoubleAnimation(fromDiu, 0, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        motion.Completed += (_, _) =>
        {
            slide.Y = 0;
            slide.BeginAnimation(TranslateTransform.YProperty, null);
        };
        slide.BeginAnimation(TranslateTransform.YProperty, motion);
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
