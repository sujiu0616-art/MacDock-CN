using System.Runtime.InteropServices;

namespace MacDock.Core;

/// <summary>
/// 前台窗口变化 / 窗口尺寸变化的即时通知（WinEvent 钩子）。
///
/// 为什么需要：全屏检测原来只靠 500ms 轮询 + Explorer 的 ABN_FULLSCREENAPP。
/// 有些播放器进全屏既不发 appbar 通知、也在两次轮询之间，于是 Dock 要等最多 0.5~1 秒才收，
/// 用户感知就是"慢半拍"。挂上这个钩子后，前台一变 / 窗口一变尺寸就立刻跑一次检测。
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const int OBJID_WINDOW = 0;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    readonly Action _onChanged;
    WinEventProc? _proc;
    IntPtr _foregroundHook, _moveHook;

    public ForegroundWatcher(Action onChanged) => _onChanged = onChanged;

    public void Start()
    {
        if (_foregroundHook != IntPtr.Zero) return;
        _proc ??= OnEvent;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_foregroundHook == IntPtr.Zero) Log.Info("ForegroundWatcher: the hook was refused");
    }

    void OnEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // 尺寸变化事件每秒有上千条：只关心"前台窗口自己"的尺寸变化
            if (eventId == EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (idObject != OBJID_WINDOW || idChild != 0) return;
                if (hwnd != GetForegroundWindow()) return;
            }
            _onChanged();
        }
        catch (Exception)
        {
            // 钩子里不许抛
        }
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
        if (_moveHook != IntPtr.Zero) { UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; }
    }
}
