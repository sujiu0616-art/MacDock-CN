using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 菜单栏的小提示气泡（托盘图标名、状态项说明）。
///
/// 为什么不用 WPF 的 ToolTip：菜单栏是"分层窗口 + 不抢焦点 + 永远置顶"的，
/// WPF 在这种窗口上算不出 PlacementTarget 的位置，气泡会跑到屏幕左上角
/// （实测：托盘图标名 "Battery Watcher" 贴在菜单栏左下角）。
/// 这里改成自己开一个小的分层窗口，位置由我们自己算，永远贴在被提示的元素正下方。
/// </summary>
internal sealed class NameTip : Window
{
    const int TipHeight = 24;
    const double MaxWidth = 320;

    static NameTip? _open;
    static int _generation;

    readonly Border _box;
    readonly TextBlock _text;

    NameTip()
    {
        Title = "MacDock Tip";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;

        _text = new TextBlock
        {
            FontFamily = Typo.Ui,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Display);
        _box = new Border
        {
            Child = _text,
            Padding = new Thickness(9, 3, 9, 3),
            CornerRadius = new CornerRadius(6),
        };
        Content = _box;
    }

    /// <summary>在某块屏幕区域下方显示提示（rect 用 DIU，屏幕坐标）。已显示时直接换字，不闪烁。</summary>
    public static void Show(string? text, Rect anchorDiu, Window? owner)
    {
        if (string.IsNullOrWhiteSpace(text)) { HideTip(); return; }
        try
        {
            var tip = _open;
            if (tip is null)
            {
                tip = new NameTip();
                _open = tip;
                tip.Show();
            }
            tip.Apply(text!, anchorDiu);
            tip.Reposition(anchorDiu);

            // 3 秒后自动收（鼠标移开也会收）
            int generation = ++_generation;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (generation == _generation) HideTip();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Showing a menu bar tip");
        }
    }

    public static void HideTip()
    {
        _generation++;
        var tip = _open;
        _open = null;
        // Close 而不是 Hide：Hide 掉的窗口对象和它的 HwndSource 会一直留着，
        // 每次悬停都新建一个 → 进程里越积越多（实测出现过 2 个 "MacDock Tip" 窗口）。
        try { tip?.Close(); } catch (Exception) { }
    }

    void Apply(string text, Rect anchor)
    {
        _text.Text = text;
        bool dark = Theme.IsDark;
        _text.Foreground = Frozen(dark ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5) : Color.FromArgb(0xFF, 0x1D, 0x1D, 0x20));
        _box.Background = Frozen(dark ? Color.FromArgb(0xF0, 0x18, 0x18, 0x1C) : Color.FromArgb(0xF2, 0xF7, 0xF7, 0xFA));
        _box.BorderBrush = Frozen(dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

        _text.Measure(new Size(MaxWidth, double.PositiveInfinity));
        Width = Math.Min(MaxWidth, _text.DesiredSize.Width + 20);
        Height = TipHeight + 2;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>给某个界面元素挂提示：位置按元素在屏幕上的实际矩形算（DIU）。</summary>
    public static void ShowFor(FrameworkElement element, string? text)
    {
        if (element is null || string.IsNullOrWhiteSpace(text)) { HideTip(); return; }
        var source = PresentationSource.FromVisual(element);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        if (scale <= 0) scale = 1;
        var topLeft = element.PointToScreen(new Point(0, 0));
        Show(text, new Rect(topLeft.X / scale, topLeft.Y / scale, element.ActualWidth, element.ActualHeight), null);
    }

    void Reposition(Rect anchor)
    {
        // anchor 是"被提示元素"的屏幕矩形（DIU）：气泡贴在它下方居中，超屏则上移
        double left = anchor.Left + anchor.Width / 2 - Width / 2;
        double top = anchor.Bottom + 6;
        var work = SystemParameters.WorkArea;
        left = Math.Max(work.Left + 4, Math.Min(left, work.Right - Width - 4));
        if (top + Height > work.Bottom) top = anchor.Top - Height - 6;
        Left = left;
        Top = top;
    }
}
