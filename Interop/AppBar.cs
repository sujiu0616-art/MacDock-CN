using System.Runtime.InteropServices;
using MacDock.Core;
using static MacDock.Interop.NativeMethods;

namespace MacDock.Interop;

/// <summary>
/// Registers the dock as a shell app bar, so maximized windows stop above it (like the macOS Dock)
/// and the shell tells us about taskbar moves and full-screen apps.
/// </summary>
internal sealed class AppBar : IDisposable
{
    readonly IntPtr _hwnd;
    bool _registered;
    uint _lastEdge = uint.MaxValue;
    RECT _lastRect;

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        CallbackMessage = RegisterWindowMessage("MacDock.AppBarMessage");
    }

    public uint CallbackMessage { get; }

    public void Register()
    {
        if (_registered) return;
        var data = NewData();
        _registered = SHAppBarMessage(ABM_NEW, ref data) != UIntPtr.Zero;
        _lastEdge = uint.MaxValue;
    }

    /// <summary>Explorer restarted: our registration is gone, register again.</summary>
    public void Reregister()
    {
        _registered = false;
        Register();
    }

    /// <summary>Moving to another monitor: drop the old reservation entirely before taking the new one.</summary>
    public void Reset()
    {
        Dispose();
        Register();
    }

    /// <summary>Sends the position again at the next <see cref="SetPosition(uint, RECT, int)"/> even if it looks unchanged (after sleep, unlock, display changes).</summary>
    public void ForgetPosition() => _lastEdge = uint.MaxValue;

    /// <summary>
    /// Reserves <paramref name="thickness"/> pixels along <paramref name="edge"/> of the monitor
    /// (0 = reserve nothing, used by auto-hide) and returns the rectangle the shell granted.
    /// The side of that rectangle facing the screen edge is where the dock sits.
    /// </summary>
    public RECT SetPosition(DockEdge edge, RECT monitor, int thickness, bool ownEdge = false)
        => SetPosition(ToAbe(edge), monitor, thickness, ownEdge);

    /// <summary>
    /// <paramref name="ownEdge"/> = 抢占这条屏幕边：跳过 ABM_QUERYPOS，直接按显示器边界要位置。
    /// 原生任务栏被 SW_HIDE 之后**仍然注册着 appbar**，它的预留还在工作区里，
    /// QUERYPOS 会把我们的矩形往上推正好一个任务栏高度（实测 Dock 悬空 72px = 用户报的"太高了"）。
    /// </summary>
    public RECT SetPosition(uint abe, RECT monitor, int thickness, bool ownEdge = false)
    {
        var data = NewData();
        data.uEdge = abe;
        data.rc = monitor;
        Shape(ref data.rc, abe, thickness);
        if (!_registered) return data.rc;

        if (!ownEdge)
        {
            SHAppBarMessage(ABM_QUERYPOS, ref data);
            Shape(ref data.rc, abe, thickness);
        }
        // Skip SETPOS when nothing changed: it would ping every other app bar and could loop.
        if (data.uEdge == _lastEdge && data.rc.Equals(_lastRect)) return data.rc;

        SHAppBarMessage(ABM_SETPOS, ref data);
        if (ownEdge)
        {
            // Explorer 仍会把我们的矩形从任务栏那段预留里"挤"出来（实测短 72px = 任务栏高度）。
            // 既然是自己占这条边，就直接按显示器边界重新算，不采信它返回的坐标。
            data.rc = monitor;
        }
        Shape(ref data.rc, abe, thickness);
        _lastEdge = data.uEdge;
        _lastRect = data.rc;
        return data.rc;
    }

    public void NotifyWindowPosChanged()
    {
        if (!_registered) return;
        var data = NewData();
        SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref data);
    }

    public void Dispose()
    {
        if (!_registered) return;
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    APPBARDATA NewData() => new()
    {
        cbSize = Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
        uCallbackMessage = CallbackMessage,
    };

    static uint ToAbe(DockEdge edge) => edge switch
    {
        DockEdge.Left => ABE_LEFT,
        DockEdge.Right => ABE_RIGHT,
        _ => ABE_BOTTOM,
    };

    static void Shape(ref RECT rc, uint abe, int thickness)
    {
        switch (abe)
        {
            case ABE_LEFT: rc.Right = rc.Left + thickness; break;
            case ABE_RIGHT: rc.Left = rc.Right - thickness; break;
            case ABE_TOP: rc.Bottom = rc.Top + thickness; break;
            default: rc.Top = rc.Bottom - thickness; break;
        }
    }
}
