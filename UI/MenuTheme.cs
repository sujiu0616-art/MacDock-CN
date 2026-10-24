using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacDock.UI;

/// <summary>
/// 右键弹出菜单的统一外观（macOS 风格）：
/// 圆角玻璃卡片 + 1px 描边 + 悬停圆角高亮 + 右对齐快捷键，深浅色都跟系统主题走。
///
/// 说明：
/// · 不挂 DropShadowEffect / BlurEffect —— WPF 里它们是软件光栅化，菜单每次弹出都重算一次；
///   阴影用一条比卡片低透明度、略微外扩的 Border 代替。
/// · 仍然用 WPF 的 ContextMenu/MenuItem，只换 Template 与配色，键盘导航、子菜单、AccessKey 全都保留。
/// </summary>
internal static class MenuTheme
{
    static bool _applied;

    static readonly Brush DarkBg = Frozen(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x20));
    static readonly Brush LightBg = Frozen(Color.FromArgb(0xF7, 0xFA, 0xFA, 0xFC));
    static readonly Brush DarkHover = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    static readonly Brush LightHover = Frozen(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
    static readonly Brush DarkRim = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    static readonly Brush LightRim = Frozen(Color.FromArgb(0x24, 0x00, 0x00, 0x00));
    static readonly Brush DarkText = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF5));
    static readonly Brush LightText = Frozen(Color.FromRgb(0x1D, 0x1D, 0x20));
    static readonly Brush DarkShadow = Frozen(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
    static readonly Brush LightShadow = Frozen(Color.FromArgb(0x14, 0x00, 0x00, 0x00));

    static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    static bool Dark => MacDock.Core.Theme.IsDark;

    static Brush Bg => Dark ? DarkBg : LightBg;
    static Brush Hover => Dark ? DarkHover : LightHover;
    static Brush Rim => Dark ? DarkRim : LightRim;
    static Brush TextBrush => Dark ? DarkText : LightText;
    static Brush Shadow => Dark ? DarkShadow : LightShadow;

    /// <summary>给一个上下文菜单套上统一外观（每次弹出前调用都安全）。</summary>
    public static void Apply(ContextMenu menu)
    {
        menu.Background = Brushes.Transparent;      // 外框由模板画
        menu.BorderThickness = new Thickness(0);
        menu.Padding = new Thickness(0);
        menu.HasDropShadow = false;
        menu.FontFamily = Typo.Ui;
        menu.FontSize = 13;
        menu.Foreground = TextBrush;

        // 不替换 ContextMenu 自己的模板：在这套分层/不激活的窗口上它不起作用（菜单窗口起来了但内容不画）。
        // 只改底色、描边、内边距 —— 菜单项与分隔线仍然用我们的模板。
        menu.Background = Bg;
        menu.BorderBrush = Rim;
        menu.BorderThickness = new Thickness(1);
        menu.Padding = new Thickness(4);
        menu.Resources[typeof(MenuItem)] = ItemTemplate();
        menu.Resources[typeof(Separator)] = SeparatorTemplate();
        _applied = true;
    }

    /// <summary>淡入 + 轻微放大，让弹出不那么"硬"（合成层动画，代价可忽略）。</summary>
    public static void FadeIn(ContextMenu menu)
    {
        menu.Opacity = 0;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90));
        menu.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    static Style MenuTemplate()
    {
        // 卡片：外扩 6px 的低透明度阴影 + 圆角玻璃板
        var border = new FrameworkElementFactory(typeof(Grid));

        var shadow = new FrameworkElementFactory(typeof(Border));
        shadow.SetValue(FrameworkElement.MarginProperty, new Thickness(6));
        shadow.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        shadow.SetValue(Border.BackgroundProperty, Shadow);
        shadow.SetValue(UIElement.IsHitTestVisibleProperty, false);
        shadow.SetValue(Panel.ZIndexProperty, 0);
        border.AppendChild(shadow);

        var card = new FrameworkElementFactory(typeof(Border));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        card.SetValue(Border.BackgroundProperty, Bg);
        card.SetValue(Border.BorderBrushProperty, Rim);
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.PaddingProperty, new Thickness(5, 5, 5, 5));
        card.SetValue(Panel.ZIndexProperty, 1);

        var host = new FrameworkElementFactory(typeof(StackPanel));
        host.SetValue(StackPanel.IsItemsHostProperty, true);
        card.AppendChild(host);
        border.AppendChild(card);

        var template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
        return new Style(typeof(ContextMenu)) { Setters = { new Setter(Control.TemplateProperty, template) } };
    }

    static Style ItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));

        // 高亮底
        var highlight = new FrameworkElementFactory(typeof(Border));
        highlight.Name = "Bd";
        highlight.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        highlight.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        highlight.SetValue(FrameworkElement.MarginProperty, new Thickness(1, 1, 1, 1));
        root.AppendChild(highlight);

        var row = new FrameworkElementFactory(typeof(Grid));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 5, 9, 5));
        var check = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        check.Name = "Check";
        check.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,4 L 3,7 L 8,0"));
        check.SetValue(System.Windows.Shapes.Path.StrokeProperty, TextBrush);
        check.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 1.6);
        check.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
        check.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
        check.SetValue(FrameworkElement.WidthProperty, 10.0);
        check.SetValue(FrameworkElement.HeightProperty, 9.0);
        check.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(UIElement.VisibilityProperty, Visibility.Hidden);

        var header = new FrameworkElementFactory(typeof(ContentPresenter));
        header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        var gesture = new FrameworkElementFactory(typeof(TextBlock));
        gesture.Name = "Gesture";
        gesture.SetValue(TextBlock.ForegroundProperty, TextBrush);
        gesture.SetValue(UIElement.OpacityProperty, 0.5);
        gesture.SetValue(FrameworkElement.MarginProperty, new Thickness(24, 0, 0, 0));
        gesture.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        gesture.SetValue(TextBlock.TextProperty, new System.Windows.Data.Binding("InputGestureText") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.AppendChild(check);
        panel.AppendChild(header);
        panel.AppendChild(gesture);
        row.AppendChild(panel);
        highlight.AppendChild(row);

        // 子菜单
        var popup = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(System.Windows.Controls.Primitives.Popup.PlacementProperty, System.Windows.Controls.Primitives.PlacementMode.Right);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.AllowsTransparencyProperty, true);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.FocusableProperty, false);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.PopupAnimationProperty, System.Windows.Controls.Primitives.PopupAnimation.None);
        popup.SetValue(System.Windows.Controls.Primitives.Popup.IsOpenProperty,
            new System.Windows.Data.Binding("IsSubmenuOpen") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var subCard = new FrameworkElementFactory(typeof(Border));
        subCard.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        subCard.SetValue(Border.BackgroundProperty, Bg);
        subCard.SetValue(Border.BorderBrushProperty, Rim);
        subCard.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        subCard.SetValue(Border.PaddingProperty, new Thickness(5));
        subCard.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
        var subHost = new FrameworkElementFactory(typeof(StackPanel));
        subHost.SetValue(StackPanel.IsItemsHostProperty, true);
        subCard.AppendChild(subHost);
        popup.AppendChild(subCard);
        root.AppendChild(popup);

        var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };
        template.Triggers.Add(new Trigger
        {
            Property = MenuItem.IsHighlightedProperty,
            Value = true,
            Setters = { new Setter(Border.BackgroundProperty, Hover, "Bd") },
        });
        template.Triggers.Add(new Trigger
        {
            Property = MenuItem.IsCheckedProperty,
            Value = true,
            Setters = { new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Check") },
        });
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.45) },
        });

        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, TextBrush));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, Typo.Ui));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 160.0));
        return style;
    }

    static Style SeparatorTemplate()
    {
        var line = new FrameworkElementFactory(typeof(Border));
        line.SetValue(FrameworkElement.HeightProperty, 1.0);
        line.SetValue(Border.BackgroundProperty, Rim);
        line.SetValue(FrameworkElement.MarginProperty, new Thickness(9, 4, 9, 4));

        var template = new ControlTemplate(typeof(Separator)) { VisualTree = line };
        return new Style(typeof(Separator)) { Setters = { new Setter(Control.TemplateProperty, template) } };
    }
}
