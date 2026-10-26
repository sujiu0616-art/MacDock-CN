using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// 灵动岛：菜单栏正中的小胶囊 + 展开后的媒体卡片。
///
/// 性能约束（用户要求"不能影响大型游戏"）：
/// · 收起状态只是一个 88×26 的圆角 Border，没有任何动画/计时器；
/// · 展开体是**独立的分层窗口**（和 PopMenu 一样），不改菜单栏窗口尺寸（那是 appbar，改尺寸会惊动 Explorer）；
/// · 动画只做 120ms 的尺寸/圆角，元素很小；
/// · 媒体信息由 <see cref="MediaWatcher"/> 事件驱动，空闲零轮询。
/// </summary>
internal sealed class IslandWindow : Window
{
    static IslandWindow? _open;

    readonly Border _card;
    readonly Image _art;
    readonly TextBlock _title;
    readonly TextBlock _artist;
    readonly TextBlock _empty;
    readonly Grid _buttons;
    readonly TextBlock _playGlyph;
    MediaWatcher? _media;
    IntPtr _ownerHandle;
    readonly System.Windows.Threading.DispatcherTimer _leaveTimer =
        new() { Interval = TimeSpan.FromMilliseconds(350) };

    public static bool IsOpen => _open is not null;

    public IslandWindow()
    {
        Title = "MacDock Island";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 10;
        Height = 10;
        Left = -32000;
        Top = -32000;

        _art = new Image { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(_art, BitmapScalingMode.HighQuality);
        var artFrame = new Border
        {
            Child = _art,
            Width = 52,
            Height = 52,
            CornerRadius = new CornerRadius(11),
            Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _title = Text("", 13, FontWeights.SemiBold);
        _artist = Text("", 11, FontWeights.Normal);
        _title.MaxWidth = 152;      // 卡片里给右侧按钮留位置
        _artist.MaxWidth = 152;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        var textColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        textColumn.Children.Add(_title);
        textColumn.Children.Add(_artist);

        _playGlyph = Text("\uE768", 15, FontWeights.Normal);
        _playGlyph.FontFamily = Typo.Icons;
        var prev = GlyphButton("\uE892", () => _media?.Previous());
        var play = GlyphButton("\uE768", () => _media?.PlayPause());
        var next = GlyphButton("\uE893", () => _media?.Next());
        _playGlyph = (TextBlock)((Border)play).Child;
        _buttons = new Grid { VerticalAlignment = VerticalAlignment.Center };
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn((UIElement)prev, 0);
        Grid.SetColumn((UIElement)play, 1);
        Grid.SetColumn((UIElement)next, 2);
        _buttons.Children.Add((UIElement)prev);
        _buttons.Children.Add((UIElement)play);
        _buttons.Children.Add((UIElement)next);

        _empty = Text("没有正在播放的媒体", 12, FontWeights.Normal);
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Margin = new Thickness(10, 0, 0, 0);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(artFrame);
        row.Children.Add(textColumn);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(row);
        grid.Children.Add(_buttons);
        Grid.SetColumn(_buttons, 1);

        var content = new Grid();
        content.Children.Add(_empty);
        content.Children.Add(grid);

        _card = new Border
        {
            Child = content,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
        };
        Content = _card;
        ApplyTheme();
        SourceInitialized += (_, _) => { };
        MouseLeave += (_, _) =>
        {
            // 给一点余地：从胶囊移到卡片上时不会立刻关掉
            _leaveTimer.Stop();
            _leaveTimer.Start();
        };
        MouseEnter += (_, _) => _leaveTimer.Stop();
    }

    TextBlock Text(string text, double size, FontWeight weight)
    {
        var t = new TextBlock { Text = text, FontFamily = Typo.Ui, FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
        return t;
    }

    UIElement GlyphButton(string glyph, Action action)
    {
        var text = Text(glyph, 15, FontWeights.Normal);
        text.FontFamily = Typo.Icons;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        var border = new Border
        {
            Child = text,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        border.MouseLeftButtonUp += (_, e) => { action(); e.Handled = true; };
        border.MouseEnter += (_, _) => border.Background = Frozen(Theme.IsDark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    void ApplyTheme()
    {
        // 固定深色玻璃：和菜单栏的材质保持一致（系统浅色时也这样，否则两种材质挨在一起很割裂）
        bool dark = true;
        _card.Background = Frozen(Color.FromArgb(0xF0, 0x1E, 0x20, 0x26));
        _card.BorderBrush = Frozen(dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        _title.Foreground = Frozen(dark ? Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF5) : Color.FromArgb(0xFF, 0x1D, 0x1D, 0x20));
        _artist.Foreground = Frozen(dark ? Color.FromArgb(0xB0, 0xE8, 0xE8, 0xEE) : Color.FromArgb(0xA0, 0x3A, 0x3A, 0x40));
        _empty.Foreground = _artist.Foreground;
        _playGlyph.Foreground = _title.Foreground;
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>在菜单栏正中下方展开（owner 为菜单栏窗口句柄）。</summary>
    public static void ShowCentered(IntPtr owner, MediaWatcher media)
    {
        var island = _open;
        if (island is null)
        {
            island = new IslandWindow { _ownerHandle = owner };
            _open = island;
            island.Show();
        }
        island._media = media;
        island.ApplyTheme();
        island.PositionUnderMenuBar(owner);
        island.Refresh();
    }

    public static void HideIsland()
    {
        var island = _open;
        _open = null;
        island?.Close();
    }

    void PositionUnderMenuBar(IntPtr owner)
    {
        try
        {
            var display = Displays.ByNameOrPrimary(null);
            double scale = display.Scale <= 0 ? 1 : display.Scale;
            var screen = display.Bounds;
            Width = 320;
            Height = 96;
            Left = (screen.Width / scale - Width) / 2 + screen.Left / scale;
            Top = (screen.Top / scale) + (48 / scale);   // 菜单栏高 48px
            Activate();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Positioning the island");
        }
    }

    /// <summary>按媒体状态刷新卡片（事件驱动调用，不轮询）。</summary>
    public void Refresh()
    {
        var media = _media;
        bool has = media is not null && media.HasSession && !string.IsNullOrWhiteSpace(media.Title);
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _buttons.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _art.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _title.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _artist.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (!has) return;
        _title.Text = media!.Title;
        _artist.Text = media.Artist;
        _art.Source = media.Art;
        _playGlyph.Text = media.IsPlaying ? "\uE769" : "\uE768";   // pause / play
    }
}
