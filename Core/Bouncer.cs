namespace MacDock.Core;

internal enum BounceKind { None, Launch, Attention }

/// <summary>
/// The Dock's icon bounce. Launch: steady bounces until the app shows a window.
/// Attention: bursts of three higher bounces with a pause, until the app is brought forward.
/// A stop request always lets the current bounce land first.
/// </summary>
internal sealed class Bouncer
{
    const double LaunchPeriod = 0.62, LaunchHeight = 0.5, LaunchTimeout = 12;
    const double AttentionTimeout = 600;  // 兜底：10 分钟（真正的停止条件是切到那个应用的窗口）
    const double AttentionPeriod = 0.5, AttentionHeight = 0.65, AttentionPause = 1.1;
    const int AttentionBounces = 3;

    double _start;
    bool _stopping;


    public BounceKind Kind { get; private set; }
    public bool IsActive => Kind != BounceKind.None;

    public void Start(BounceKind kind, double now)
    {
        if (kind == BounceKind.None) return;
        if (Kind == kind)
        {
            _stopping = false;
            return;
        }
        Kind = kind;
        _start = now;
        _stopping = false;
    }

    public void Stop(double now)
    {
        // 立即把 Kind 清成 None。以前只是标记 _stopping、等 Offset() 内部再清，
        // 而 Offset 只在渲染活跃时被调用 —— 渲染一停，Kind 就永远挂着，
        // 于是多个图标同时 IsActive，呼吸被算在最旧的那个上（用户看到"漂移到别的图标"）。
        // 弹跳本来就是"点一下就有反馈"的短动画，直接收掉没有副作用。
        Kind = BounceKind.None;
        _stopping = false;
    }

    /// <summary>Current lift, as a fraction of the icon size.</summary>
    public double Offset(double now)
    {
        if (!IsActive) return 0;
        double t = now - _start;
        if (Kind == BounceKind.Launch && t > LaunchTimeout && !_stopping) Stop(now);
        // Attention 原本没有超时，完全依赖"窗口被激活"来 Stop。用户如果用别的方式把窗口
        // 调到前台而 tracker 没抓到（或者根本没切），呼吸就会一直挂在那个图标上。
        if (Kind == BounceKind.Attention && t > AttentionTimeout && !_stopping)
        {
            Log.Info($"Bounce: attention timeout after {t:0.0}s");
            Stop(now);
        }

        bool resting = Kind == BounceKind.Attention && t % Cycle >= AttentionBounces * AttentionPeriod;
        if (resting) return 0;

        double period = Kind == BounceKind.Launch ? LaunchPeriod : AttentionPeriod;
        double height = Kind == BounceKind.Launch ? LaunchHeight : AttentionHeight;
        double phase = (Kind == BounceKind.Launch ? t : t % Cycle) % period / period;
        return height * 4 * phase * (1 - phase); // a parabola: a real jump
    }

    static double Cycle => AttentionBounces * AttentionPeriod + AttentionPause;

    long BounceIndex(double t) => Kind == BounceKind.Launch
        ? (long)(t / LaunchPeriod)
        : (long)(t / Cycle) * AttentionBounces + Math.Min(AttentionBounces - 1, (long)(t % Cycle / AttentionPeriod));
}
