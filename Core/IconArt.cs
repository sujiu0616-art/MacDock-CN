using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacDock.Core;

/// <summary>
/// 图标美术：统一成 macOS 那种圆角方块，以及画一个真正的"启动台"图标。
///
/// Windows 的图标有方有圆、有带透明边的，直接摆一排大小参差；macOS 的 Dock 里每个图标都是同一套
/// squircle（超椭圆圆角，约 22% 半径），所以这里统一裁一次。裁切在 256 px 上做，缩到 56 DIU 时不会糊。
/// </summary>
internal static class IconArt
{
    /// <summary>统一圆角：把任意图标裁成圆角方块；失败就原样返回，绝不因为美术处理丢了图标。</summary>
    // 176 足够：Dock 图标放大到最大也只有 76 DIU（150% 下 114px），256 是 2.2 倍过采样、每张多占 150KB。
    public static ImageSource? Squircle(ImageSource? icon, int size = 176)
    {
        if (icon is null) return null;
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double radius = size * 0.225;              // macOS 的 22.37%
                var clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
                dc.PushClip(clip);
                // 原图按"填满"摆放：方图标铺满，长条图标居中裁切，缩小时不会留白边
                double scale = Math.Max(size / icon.Width, size / icon.Height);
                double w = icon.Width * scale, h = icon.Height * scale;
                dc.DrawImage(icon, new Rect((size - w) / 2, (size - h) / 2, w, h));
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rounding an icon");
            return icon;
        }
    }

    static BitmapSource? _launchpad;

    /// <summary>
    /// 启动台的图标：macOS 的 Launchpad 是一块浅银灰的圆角方块，里面 3×3 个更亮的圆角小格。
    /// 原来这里是 Windows 徽标（左键已经开启动台了，图标还画成开始按钮会让人误会）。
    /// </summary>
    public static BitmapSource Launchpad
    {
        get
        {
            if (_launchpad is not null) return _launchpad;
            const int size = 256;
            double radius = size * 0.225;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var body = new LinearGradientBrush(
                    Color.FromRgb(0xF7, 0xF8, 0xFA), Color.FromRgb(0xD3, 0xD7, 0xDF), new Point(0, 0), new Point(0, 1));
                dc.DrawRoundedRectangle(body, null, new Rect(0, 0, size, size), radius, radius);

                // 上缘高光，和 Dock 玻璃的观感对齐
                var rim = new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), 3);
                dc.DrawRoundedRectangle(null, rim, new Rect(1.5, 1.5, size - 3, size - 3), radius - 1.5, radius - 1.5);

                double cell = size * 0.155, gap = size * 0.055;
                double span = cell * 3 + gap * 2;
                double first = (size - span) / 2;
                var cellBrush = new LinearGradientBrush(
                    Color.FromRgb(0x8E, 0x93, 0x9E), Color.FromRgb(0x6E, 0x73, 0x7D), new Point(0, 0), new Point(0, 1));
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        double x = first + col * (cell + gap), y = first + row * (cell + gap);
                        dc.DrawRoundedRectangle(cellBrush, null, new Rect(x, y, cell, cell), cell * 0.28, cell * 0.28);
                    }
                }
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            _launchpad = bitmap;
            return bitmap;
        }
    }
}
