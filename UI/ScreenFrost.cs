using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// 屏幕磨砂：在启动台**打开之前**把屏幕抓一张、缩小、模糊一次，当成它的背景。
///
/// 为什么这么做：macOS 的启动台是"全屏 + 强模糊"的 vibrancy，背后是桌面/窗口的真实内容。
/// Windows 给不了我们这个（DWM 的系统模糊在这台 Win11 上渲染成不透明深色，accent 模糊同样不работ），
/// 而启动台一旦打开就铺满整屏、背后的东西不再变化 —— 所以抓一次屏、模糊一次就够了：
/// 真透明、零持续开销，也不会像"裁壁纸"那样在窗口跑到后面时露馅。
/// </summary>
internal static class ScreenFrost
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// 抓 <paramref name="bounds"/>（物理像素）这块屏幕，缩小到 1/<paramref name="divisor"/>，
    /// 用 <paramref name="blurRadius"/> 模糊一次，返回可直接拉伸铺满的冻结位图。
    /// 失败返回 null（调用方退回半透明 tint）。
    /// </summary>
    public static BitmapSource? Capture(RECT bounds, int divisor = 4, double blurRadius = 18)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        BitmapSource? raw = null;
        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, bounds.Width, bounds.Height);
            if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
            old = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, bounds.Width, bounds.Height, screen, bounds.Left, bounds.Top, SRCCOPY)) return null;

            raw = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            raw.Freeze();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capturing the screen for the launchpad");
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero && memory != IntPtr.Zero) SelectObject(memory, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }

        try
        {
            // 缩到 1/divisor 再模糊：像素少 16 倍，磨砂效果一样，代价几乎为零
            int w = Math.Max(1, bounds.Width / divisor), h = Math.Max(1, bounds.Height / divisor);
            var small = new TransformedBitmap(raw, new ScaleTransform((double)w / bounds.Width, (double)h / bounds.Height));
            small.Freeze();

            var visual = new DrawingVisual();
            // 模糊会从边缘往里吸透明，所以画得比目标区域大一圈再裁回来。
            visual.Effect = new BlurEffect { Radius = blurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            using (var dc = visual.RenderOpen())
            {
                var brush = new ImageBrush(small) { Stretch = Stretch.Fill };
                brush.Freeze();
                double over = blurRadius * 2;
                dc.DrawRectangle(brush, null, new Rect(-over, -over, w + over * 2, h + over * 2));
            }
            var blurred = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            blurred.Render(visual);
            blurred.Freeze();
            return blurred;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Blurring the screen capture");
            return raw;
        }
    }
}
