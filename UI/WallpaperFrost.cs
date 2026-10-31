using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using MacDock.Core;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// Frosted glass painted from the desktop wallpaper instead of the OS backdrop.
///
/// Why: on this Windows 11 build both the DWM system backdrop and the older accent blur come out
/// almost opaque dark (measured: the bar over a bright wallpaper read at luminance 69 against 226 of
/// the wallpaper itself), so a "glass" bar looked like a solid strip. The menu bar and the dock plate
/// both reserve their strip as an app bar, so what sits behind them is the wallpaper — cropping and
/// blurring it is both cheaper and closer to macOS, where the bar plainly shows the wallpaper through.
/// </summary>
internal static class WallpaperFrost
{
    static BitmapSource? _image;
    static string? _path;
    static int _pixelsWide;
    static DateTime _readAt;

    /// <summary>
    /// The wallpaper decoded to <paramref name="pixelsWide"/> wide, or null when Windows has none.
    ///
    /// 关键：以前是按原始分辨率解码的。这台机器的壁纸是 6554×4096 的 PNG —— 解成 BGRA 要
    /// 6554×4096×4 ≈ 102 MB 常驻，而屏幕上只用到 2560 宽的一条带。DecodePixelWidth 让 WPF
    /// 在解码阶段就缩到目标宽度（顺带解码也快得多），2560 宽只需 ≈16 MB。
    /// </summary>
    static BitmapSource? Load(int pixelsWide)
    {
        var path = CurrentPath();
        if (path is null) return null;
        pixelsWide = Math.Clamp(pixelsWide, 64, 8192);
        // Re-read at most once a minute: a slideshow wallpaper changes under us, a fixed one does not.
        if (_image is not null && _path == path && _pixelsWide == pixelsWide && DateTime.UtcNow - _readAt < TimeSpan.FromMinutes(1))
            return _image;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = pixelsWide;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            if (_pixelsWide != pixelsWide)
                Log.Info($"Wallpaper: decoded at {image.PixelWidth}x{image.PixelHeight} (requested {pixelsWide}px wide, ~{image.PixelWidth * image.PixelHeight * 4 / 1048576} MB)");

            _image = image;
            _path = path;
            _pixelsWide = pixelsWide;
            _readAt = DateTime.UtcNow;
            return image;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the wallpaper");
            return null;
        }
    }

    static string? CurrentPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string fromRegistry && fromRegistry.Length > 0 && File.Exists(fromRegistry))
                return fromRegistry;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the wallpaper path");
        }
        return null;
    }

    /// <summary>
    /// A crop of the wallpaper behind <paramref name="screen"/> (physical pixels), ready to fill a
    /// surface of the same size. The wallpaper is stretched to cover the monitor, like Windows does.
    /// </summary>
    /// <param name="luminance">Mean brightness of the crop (0-255), so the caller can pick a legible
    /// foreground: a bright wallpaper turns the glass bright, and white text on it would vanish.</param>
    public static ImageBrush? BrushFor(RECT screen, Display display, out double luminance)
    {
        luminance = 0;
        var monitor = display.Bounds;
        if (monitor.Width <= 0 || monitor.Height <= 0) return null;

        // 只按这块屏的分辨率解码：屏幕上永远不需要比这更细的壁纸
        var image = Load(monitor.Width);
        if (image is null || image.PixelWidth == 0 || image.PixelHeight == 0) return null;

        // Wallpaper pixels per screen pixel, then the crop inside the image.
        double sx = image.PixelWidth / (double)monitor.Width, sy = image.PixelHeight / (double)monitor.Height;
        double scale = Math.Max(sx, sy); // "fill": the larger ratio wins, the rest is cropped away
        double offsetX = (image.PixelWidth - monitor.Width * scale) / 2;
        double offsetY = (image.PixelHeight - monitor.Height * scale) / 2;

        double x = offsetX + (screen.Left - monitor.Left) * scale;
        double y = offsetY + (screen.Top - monitor.Top) * scale;
        double w = Math.Max(1, screen.Width * scale);
        double h = Math.Max(1, screen.Height * scale);
        if (x < 0 || y < 0 || x + w > image.PixelWidth + 0.5 || y + h > image.PixelHeight + 0.5)
        {
            x = Math.Clamp(x, 0, Math.Max(0, image.PixelWidth - w));
            y = Math.Clamp(y, 0, Math.Max(0, image.PixelHeight - h));
        }

        var crop = new CroppedBitmap(image, new Int32Rect((int)Math.Round(x), (int)Math.Round(y),
            (int)Math.Round(Math.Min(w, image.PixelWidth - x)), (int)Math.Round(Math.Min(h, image.PixelHeight - y))));
        crop.Freeze();

        var brush = new ImageBrush(crop) { Stretch = Stretch.Fill, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        brush.Freeze();
        luminance = MeanLuminance(crop);
        return brush;
    }

    /// <summary>Average brightness of an image, measured on a 16x16 thumbnail.</summary>
    static double MeanLuminance(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(source, new ScaleTransform(
                Math.Min(1, 16.0 / source.PixelWidth), Math.Min(1, 16.0 / source.PixelHeight)));
            small.Freeze();
            var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            int stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            double sum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]; // BGR(A)
            return pixels.Length == 0 ? 0 : sum / (pixels.Length / 4);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Measuring the wallpaper brightness");
            return 0;
        }
    }

    /// <summary>The blurred wallpaper plus a tint: the two layers every glass surface here is made of.</summary>
    public static Grid Build(out Image surface, out Border tint, double blurRadius)
    {
        surface = new Image { Stretch = Stretch.Fill };
        surface.Effect = new BlurEffect { Radius = blurRadius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        tint = new Border();
        var grid = new Grid { ClipToBounds = true };
        grid.Children.Add(surface);
        grid.Children.Add(tint);
        return grid;
    }
}
