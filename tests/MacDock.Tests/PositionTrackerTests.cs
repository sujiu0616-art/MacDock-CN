using MacDock.Core;

namespace MacDock.Tests;

/// <summary>位置推进器的状态机测试：推进、暂停、封顶、单曲循环回绕。</summary>
public class PositionTrackerTests
{
    static PositionTracker NewTracker(TimeSpan duration, bool known = true)
    {
        var tracker = new PositionTracker();
        tracker.Reanchor(TimeSpan.Zero, nowMs: 1_000, duration: duration, known: known);
        tracker.SetPlayback(advancing: true, rate: 1.0, duration: duration, nowMs: 1_000);
        return tracker;
    }

    [Fact]
    public void Advances_with_elapsed_time()
    {
        var tracker = NewTracker(TimeSpan.FromMinutes(4));
        Assert.Equal(2, tracker.Current(nowMs: 3_000).TotalSeconds, 2);
        Assert.Equal(12, tracker.Current(nowMs: 13_000).TotalSeconds, 2);
    }

    [Fact]
    public void Pause_freezes_position()
    {
        var tracker = NewTracker(TimeSpan.FromMinutes(4));
        tracker.SetPlayback(advancing: false, rate: 1.0, duration: TimeSpan.FromMinutes(4), nowMs: 11_000);
        Assert.Equal(10, tracker.Current(nowMs: 60_000).TotalSeconds, 1);
    }

    [Fact]
    public void Unknown_position_is_not_advanced_or_rewound()
    {
        var tracker = NewTracker(TimeSpan.FromMinutes(4), known: false);
        Assert.Equal(0, tracker.Current(nowMs: 600_000).TotalSeconds, 1);
        Assert.False(tracker.Tick(600_000));
        Assert.Equal(0, tracker.LoopRewinds);
    }

    [Fact]
    public void Single_track_loop_rewinds_after_ceiling_hold()
    {
        var duration = TimeSpan.FromSeconds(200);
        var tracker = NewTracker(duration);
        tracker.Reanchor(TimeSpan.FromSeconds(198), nowMs: 1_000, duration: duration, known: true);
        tracker.SetPlayback(advancing: true, rate: 1.0, duration: duration, nowMs: 1_000);

        // 到顶：第一次 tick 只记录贴顶时刻
        Assert.False(tracker.Tick(3_000));
        // 1.5 秒内还不算循环
        Assert.False(tracker.Tick(4_000));
        // 超过 1.5 秒 → 回绕
        Assert.True(tracker.Tick(4_600));
        Assert.Equal(1, tracker.LoopRewinds);
        // 回绕后位置应该很小
        Assert.InRange(tracker.Current(4_600).TotalSeconds, 0, 0.2);
    }

    [Fact]
    public void Duration_from_api_is_used_for_ceiling()
    {
        var tracker = NewTracker(duration: TimeSpan.Zero, known: true);
        tracker.SetDuration(TimeSpan.FromSeconds(180));
        tracker.Reanchor(TimeSpan.FromSeconds(179), nowMs: 1_000, duration: TimeSpan.FromSeconds(180), known: true);
        tracker.SetPlayback(advancing: true, rate: 1.0, duration: TimeSpan.FromSeconds(180), nowMs: 1_000);
        Assert.False(tracker.Tick(2_000));
        Assert.True(tracker.Tick(4_000));
        Assert.Equal(1, tracker.LoopRewinds);
    }
}
