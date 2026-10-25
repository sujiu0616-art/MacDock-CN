using System.Windows.Threading;
using MacDock.Core;

namespace MacDock.UI;

/// <summary>
/// UI 线程卡顿探针：后台线程每 200ms 问一次 UI 线程"你还在吗"。
/// 只有当 UI 线程超过 <see cref="WarnMs"/> 没回应（或就绪队列里堆了多帧）时才写日志。
///
/// 用途：用户报"软件无响应、点一下托盘就好"时，靠这行日志直接判断
/// 是不是**我们的 UI 线程卡了**（如果是，日志会有 `UiWatchdog: stall Nms`）。
/// 平时零开销：一个 200ms 的后台计时器 + 一次 Dispatcher 投递。
/// </summary>
internal static class UiWatchdog
{
    const int ProbeMs = 200;
    const int WarnMs = 800;

    static Dispatcher? _dispatcher;
    static Thread? _thread;
    static long _postedTicks;
    static long _answeredTicks;
    static long _lastWarnTicks;

    public static void Start(Dispatcher dispatcher)
    {
        if (_thread is not null) return;
        _dispatcher = dispatcher;
        _answer();

        _thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(ProbeMs);
                long posted = Environment.TickCount64;
                _postedTicks = posted;

                var target = _dispatcher;
                if (target is null) return;
                target.BeginInvoke(new Action(_answer), DispatcherPriority.Background);

                Thread.Sleep(ProbeMs);
                // UI 线程在 WarnMs 内没回应 → 记一条（同一个卡顿只记一次）
                if (_answeredTicks < posted && Environment.TickCount64 - posted >= WarnMs
                    && posted - _lastWarnTicks > WarnMs)
                {
                    _lastWarnTicks = posted;
                    Log.Info($"UiWatchdog: stall >{WarnMs}ms (UI thread did not answer a Background post)");
                }
            }
        })
        {
            IsBackground = true,
            Name = "MacDock ui watchdog",
        };
        _thread.Start();
    }

    static void _answer() => _answeredTicks = Environment.TickCount64;
}
