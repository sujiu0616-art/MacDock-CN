using MacDock.Core;

namespace MacDock.Tests;

/// <summary>单曲循环检测：位置贴天花板且仍在播放 → 归零重来。</summary>
public class MediaLoopTests
{
    [Fact]
    public void At_ceiling_when_within_one_second_of_duration()
    {
        var duration = TimeSpan.FromMinutes(4);
        Assert.True(MediaRules.IsAtCeiling(TimeSpan.FromSeconds(239), duration));
        Assert.True(MediaRules.IsAtCeiling(TimeSpan.FromSeconds(238.9), duration));
        Assert.False(MediaRules.IsAtCeiling(TimeSpan.FromSeconds(200), duration));
    }

    [Fact]
    public void Unknown_duration_is_never_at_ceiling()
        => Assert.False(MediaRules.IsAtCeiling(TimeSpan.FromSeconds(10), TimeSpan.Zero));

    [Fact]
    public void Loop_restarts_only_after_threshold()
    {
        Assert.False(MediaRules.ShouldRestartLoop(ceilingSinceMs: 0, nowMs: 10_000));
        Assert.False(MediaRules.ShouldRestartLoop(ceilingSinceMs: 10_000, nowMs: 10_500));
        Assert.True(MediaRules.ShouldRestartLoop(ceilingSinceMs: 10_000, nowMs: 11_500));
        Assert.True(MediaRules.ShouldRestartLoop(ceilingSinceMs: 10_000, nowMs: 12_000));
    }

    [Fact]
    public void Projected_position_at_ceiling_then_reset_behaves_like_loop()
    {
        var duration = TimeSpan.FromSeconds(200);
        var atEnd = MediaRules.ProjectPosition(TimeSpan.FromSeconds(199), anchorAtMs: 1, nowMs: 60_000, rate: 1.0,
                                               duration: duration, advancing: true);
        Assert.True(MediaRules.IsAtCeiling(atEnd, duration));

        // 循环重启：锚点归零后同一时刻应算出很小的时间
        var restarted = MediaRules.ProjectPosition(TimeSpan.Zero, anchorAtMs: 60_000, nowMs: 60_500, rate: 1.0,
                                                   duration: duration, advancing: true);
        Assert.InRange(restarted.TotalSeconds, 0.4, 0.6);
    }
}
