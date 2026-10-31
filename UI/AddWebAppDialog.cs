using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacDock.UI;

/// <summary>
/// 轻量输入框：名称 + 网址，用来把网页应用加进 Dock。
/// 用独立的小窗口而不是 WPF 的 InputBox（.NET 里没有），也不去抢 Dock 的焦点逻辑。
/// </summary>
internal sealed class AddWebAppDialog : Window
{
    readonly TextBox _name;
    readonly TextBox _url;

    public string AppName { get; private set; } = "";
    public string Url { get; private set; } = "";

    public AddWebAppDialog()
    {
        Title = "添加网页应用";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nameLabel = new TextBlock { Text = "名称", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
        _name = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8) };
        var urlLabel = new TextBlock { Text = "网址", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 8) };
        _url = new TextBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 8), Text = "https://" };

        var hint = new TextBlock
        {
            Text = "会用 Chrome/Edge 的应用模式打开（无地址栏），可随时在 Dock 右键移除。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4) };
        var ok = new Button { Content = "添加", Width = 80, Padding = new Thickness(0, 4, 0, 4), IsDefault = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        ok.Click += (_, _) =>
        {
            string url = _url.Text.Trim();
            if (url.Length == 0) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            Url = url;
            AppName = string.IsNullOrWhiteSpace(_name.Text) ? new Uri(url).Host : _name.Text.Trim();
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        Grid.SetRow(nameLabel, 0); Grid.SetColumn(nameLabel, 0); grid.Children.Add(nameLabel);
        Grid.SetRow(_name, 0); Grid.SetColumn(_name, 1); grid.Children.Add(_name);
        Grid.SetRow(urlLabel, 1); Grid.SetColumn(urlLabel, 0); grid.Children.Add(urlLabel);
        Grid.SetRow(_url, 1); Grid.SetColumn(_url, 1); grid.Children.Add(_url);
        Grid.SetRow(hint, 2); Grid.SetColumnSpan(hint, 2); grid.Children.Add(hint);
        Grid.SetRow(buttons, 3); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => { _url.Focus(); _url.SelectAll(); };
    }

    /// <summary>弹出对话框；用户取消返回 null。</summary>
    public static (string Name, string Url)? Ask(Window? owner)
    {
        var dialog = new AddWebAppDialog();
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? (dialog.AppName, dialog.Url) : null;
    }
}
