using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// A non-layered, never-activated window with the real Windows 11 acrylic backdrop from DWM
/// (layered windows cannot have it). WPF content drawn on it sits on top of the glass.
/// </summary>
internal abstract class AcrylicWindow : Window
{
    readonly bool _rounded;

    protected AcrylicWindow(bool rounded)
    {
        _rounded = rounded;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }

    /// <summary>
    /// 是否让 DWM 给整扇窗加"系统背景材料"（mica/acrylic）。这台 Win11 上它会渲染成一块不透明的灰板，
    /// 需要真透明的窗口（启动台）把它关掉，只保留逐像素 alpha。
    /// </summary>
    protected virtual bool UseSystemBackdrop => true;

    public IntPtr Handle { get; private set; }

    RECT _bounds;

    // Corner shaping: DWM only knows its own fixed radius (ROUND / ROUNDSMALL), while the Dock's plate
    // needs one that follows its height. So the glass is cut to a window region instead.
    int _radiusPx;
    int _regionWidth = -1, _regionHeight = -1, _regionRadius = -1;

    /// <summary>Moves the glass, in physical screen pixels.</summary>
    public void SetBounds(RECT r)
    {
        if (Handle == IntPtr.Zero || r.Equals(_bounds)) return;
        _bounds = r;
        SetWindowPos(Handle, IntPtr.Zero, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height),
            SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);
        ApplyRegion();
        OnBoundsChanged(r);
    }

    /// <summary>The glass rectangle in physical screen pixels (valid after the first SetBounds).</summary>
    protected RECT GlassBounds => _bounds;

    /// <summary>Called after the glass moved or resized, for windows that paint their own backdrop.</summary>
    protected virtual void OnBoundsChanged(RECT bounds) { }

    /// <summary>Corner radius of the glass in physical pixels; 0 leaves the window square.</summary>
    public void SetGlassRadius(int pixels)
    {
        pixels = Math.Max(0, pixels);
        if (pixels == _radiusPx) return;
        _radiusPx = pixels;
        ApplyRegion();
    }

    /// <summary>
    /// Cuts the window to its rounded outline. A window region is not antialiased, so the caller
    /// draws a 1px rim over its edge, which hides the steps.
    /// </summary>
    void ApplyRegion()
    {
        if (Handle == IntPtr.Zero) return;
        int w = Math.Max(1, _bounds.Width), h = Math.Max(1, _bounds.Height);
        if (w == _regionWidth && h == _regionHeight && _radiusPx == _regionRadius) return;
        _regionWidth = w;
        _regionHeight = h;
        _regionRadius = _radiusPx;

        IntPtr region = _radiusPx > 0
            ? GlassInterop.CreateRoundRectRgn(0, 0, w + 1, h + 1, _radiusPx * 2, _radiusPx * 2)
            : IntPtr.Zero;
        if (GlassInterop.SetWindowRgn(Handle, region, true) != 0) return; // the system owns the region from now on
        if (region != IntPtr.Zero) GlassInterop.DeleteObject(region);
        _regionRadius = -1; // not applied: try again next time, the glass stays rectangular meanwhile
        Log.Info("Glass region refused, keeping square corners");
    }

    /// <summary>
    /// Real backdrop blur for a window that is not rectangular: DWM drops its system backdrop as soon
    /// as a window region is set (the corners then show the desktop sharp through the tint), while the
    /// older accent policy keeps blurring whatever shape the region has.
    /// </summary>
    protected void UseAccentBlur()
    {
        if (Handle == IntPtr.Zero) return;
        int none = DWMSBT_NONE;
        DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

        var policy = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_BLURBEHIND,
            AccentFlags = 0,
            // Near-zero ALPHA in AABBGGRR, not 0: DWM reads a 0 gradient as opaque black, which is what
            // made the glass a dark plate. 0x01FFFFFF is the usual "blur without a tint" value.
            GradientColor = 0x01FFFFFF, // the tint itself is the plate's own WPF brush
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = buffer, SizeOfData = size };
            if (GlassInterop.SetWindowCompositionAttribute(Handle, ref data) == 0)
                Log.Info("Glass: the accent blur was refused, the plate stays see-through");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Interop declared here rather than in <c>Interop/NativeMethods.cs</c>, which this part of the
    /// shell does not own; both calls only concern the glass windows of this file.
    /// </summary>
    static class GlassInterop
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

        [DllImport("user32.dll")]
        public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    }

    const int WCA_ACCENT_POLICY = 19;
    const int ACCENT_ENABLE_BLURBEHIND = 3;
    const int DWMSBT_NONE = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>After sleep, unlock or a display change DWM may draw the glass as inactive (a flat colour).</summary>
    public void Reactivate()
    {
        if (Handle != IntPtr.Zero) SendMessage(Handle, WM_NCACTIVATE, 1, IntPtr.Zero);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        // Moved onto a monitor with another scale, WPF resizes the window to what it thinks it should be.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var wanted = _bounds;
            _bounds = default;
            if (wanted.Width > 0) SetBounds(wanted);
        });
        MakeToolWindow(Handle);
        // Without a system menu DWM draws no caption buttons on the glass.
        long style = (long)GetWindowLongPtr(Handle, GWL_STYLE);
        SetWindowLongPtr(Handle, GWL_STYLE, (IntPtr)(style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)));

        var source = HwndSource.FromHwnd(Handle)!;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook(AcrylicWndProc);

        if (UseSystemBackdrop)
        {
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
        int corner = _rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        ApplyTheme();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // DWM swaps acrylic for a flat colour on inactive windows, and this one is never activated.
        SendMessage(Handle, WM_NCACTIVATE, 1, IntPtr.Zero);
    }

    /// <summary>Dark or light glass; <paramref name="border"/> is a COLORREF (0x00BBGGRR) or DWMWA_COLOR_NONE.</summary>
    protected void ApplyGlassTheme(int border)
    {
        if (Handle == IntPtr.Zero) return;
        int dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    public abstract void ApplyTheme();

    IntPtr AcrylicWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return MA_NOACTIVATE;
            case WM_NCACTIVATE when wParam == IntPtr.Zero:
                // Keep the frame (and so the acrylic) in its active look.
                handled = true;
                return DefWindowProc(hwnd, msg, 1, lParam);
            case WM_WINDOWPOSCHANGING:
                // Every resize is clamped to the minimum size of a captioned window (39 px high at
                // 100%; WPF records it before any hook can lower it): the glass must be free to be
                // thinner (menu bar) or tiny (dock plate). WPF's layout then no longer matches the
                // window, so these windows hold nothing but a tint; content goes in a window above.
                handled = true;
                return IntPtr.Zero;
            case WM_GETMINMAXINFO:
                // The same limit when the system resizes the window.
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize = new POINT { X = 1, Y = 1 };
                Marshal.StructureToPtr(info, lParam, false);
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }
}
