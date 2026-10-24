using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MacDock.UI;

/// <summary>
/// 一个地方管字体与文字渲染。
///
/// 为什么要专门管：Dock、菜单栏、启动台都是 <c>AllowsTransparency</c> 的分层窗口，Windows 在这种窗口上
/// 不给 ClearType，WPF 只能走灰度抗锯齿；再叠加默认的 Ideal 排版提示（按矢量对齐而不是按像素对齐），
/// 12-13 DIU 的小字在 150% 缩放下就发虚。这里统一改成 Display 提示 + 灰度 + 布局对齐像素。
/// 字体串里显式列出中文脸（雅黑 UI）：只写 Segoe UI 时，中文靠 WPF 的复合字体回落，个别字会掉到衬线体。
/// </summary>
internal static class Typo
{
    /// <summary>拉丁字用 Segoe UI Variable，中文用雅黑 UI（本机没有苹方）。</summary>
    public static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    /// <summary>系统图标字形（菜单栏状态、启动台的放大镜）。</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>给窗口根节点设一次：附加属性会继承给所有子元素。</summary>
    public static void Crisp(Window window)
    {
        window.UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Grayscale);
    }

    /// <summary>单独给某个文本块设（放在非继承链里的情况）。</summary>
    public static void Crisp(TextBlock text)
    {
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(text, TextRenderingMode.Grayscale);
    }
}
