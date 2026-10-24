using System.Windows;
using MacDock.Interop;
using static MacDock.Interop.NativeMethods;

namespace MacDock.UI;

/// <summary>
/// Dock 与启动台的右键菜单：<see cref="PopMenu"/> 的薄封装。
///
/// 从 WPF 的 ContextMenu 换过来的原因：它的默认外观是 Windows 那套白底直角菜单，
/// 而自定义模板在这套"分层 + 不激活"的窗口上根本不绘制。现在外观与交互都在 <see cref="PopMenu"/> 里，
/// 这里只保留两件它管不了的事：弹出前把前台交给 Dock（否则菜单收不到鼠标），关掉后把前台还回去。
/// </summary>
internal sealed class DockMenu
{
    readonly PopMenu _menu = new();
    IntPtr _ownerHwnd, _previousForeground;
    bool _actionTaken;

    public DockMenu() => _menu.Closed += () =>
    {
        // Hand the focus back unless a command moved it somewhere on purpose.
        if (!_actionTaken && _previousForeground != IntPtr.Zero && GetForegroundWindow() == _ownerHwnd)
            SetForegroundWindow(_previousForeground);
        Closed?.Invoke();
    };

    public event Action? Closed;

    public void Add(string header, Action action, bool isChecked = false, bool enabled = true, string? gesture = null) =>
        _menu.Add(header, () => { _actionTaken = true; action(); }, isChecked, enabled, gesture);

    public void AddSeparator() => _menu.AddSeparator();

    /// <summary>A greyed-out caption above a group of items.</summary>
    public void AddHeader(string text) => _menu.AddHeader(text);

    /// <summary>An item that opens a submenu; fill the returned section.</summary>
    public PopMenu AddSubmenu(string header) => _menu.AddSubmenu(header);

    public void Show(Window owner, IntPtr ownerHwnd, IntPtr previousForeground)
    {
        _ownerHwnd = ownerHwnd;
        _previousForeground = previousForeground;
        SetForegroundWindow(ownerHwnd); // allowed: the right click was the last input

        if (!GetCursorPos(out var p)) return;
        _menu.Show(p.X, p.Y);
    }

    /// <summary>在指定屏幕坐标弹出（--menu 预览用）。</summary>
    public void ShowAt(IntPtr ownerHwnd, int x, int y)
    {
        _ownerHwnd = ownerHwnd;
        _previousForeground = GetForegroundWindow();
        _menu.Show(x, y);
    }
}
