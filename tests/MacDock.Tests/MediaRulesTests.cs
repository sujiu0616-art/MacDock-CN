using MacDock.Core;

namespace MacDock.Tests;

/// <summary>
/// 灵动岛媒体数据层的纯规则测试：会话选择、位置外推、位置可信判定、元数据兜底、源进程存活。
/// 这些是"多会话选对""位置正确""退出不残留"的直接证据。
/// </summary>
public class MediaRulesTests
{
    static MediaRules.SessionSnapshot Snap(string appId, int status, long updated = 0)
        => new(appId, status, updated);

    [Fact]
    public void Playing_session_wins_over_current()
    {
        // 浏览器是"最近活跃"，但网易云在播放 → 选网易云
        var sessions = new[]
        {
            Snap("msedge.exe", MediaRules.StatusPaused, updated: 900),
            Snap("cloudmusic.exe", MediaRules.StatusPlaying, updated: 100),
        };
        Assert.Equal(1, MediaRules.PickBestIndex(sessions));
    }

    [Fact]
    public void When_nothing_plays_most_recently_updated_wins()
    {
        var sessions = new[]
        {
            Snap("cloudmusic.exe", MediaRules.StatusPaused, updated: 100),
            Snap("msedge.exe", MediaRules.StatusPaused, updated: 800),
        };
        Assert.Equal(1, MediaRules.PickBestIndex(sessions));
    }

    [Fact]
    public void Empty_session_list_returns_minus_one()
        => Assert.Equal(-1, MediaRules.PickBestIndex(ReadOnlySpan<MediaRules.SessionSnapshot>.Empty));

    [Theory]
    [InlineData(MediaRules.StatusPlaying, "Playing")]
    [InlineData(MediaRules.StatusPaused, "Paused")]
    [InlineData(MediaRules.StatusChanging, "Buffering")]
    [InlineData(MediaRules.StatusStopped, "Stopped")]
    [InlineData(MediaRules.StatusClosed, "Stopped")]
    public void States_map_to_four_kinds(int status, string expected)
        => Assert.Equal(expected, MediaRules.DescribeState(status));

    [Fact]
    public void Position_extrapolates_by_elapsed_time_times_rate()
    {
        var anchor = TimeSpan.FromSeconds(10);
        var projected = MediaRules.ProjectPosition(anchor, anchorAtMs: 1000, nowMs: 3000, rate: 1.0,
                                                  duration: TimeSpan.FromMinutes(4), advancing: true);
        Assert.Equal(12, projected.TotalSeconds, 2);
    }

    [Fact]
    public void Position_extrapolates_faster_with_rate_2()
    {
        var projected = MediaRules.ProjectPosition(TimeSpan.Zero, 0 + 1, 1 + 2000, 2.0,
                                                  TimeSpan.FromMinutes(4), advancing: true);
        Assert.Equal(4, projected.TotalSeconds, 2);
    }

    [Fact]
    public void Paused_position_does_not_advance()
    {
        var anchor = TimeSpan.FromSeconds(42);
        var projected = MediaRules.ProjectPosition(anchor, 1000, 60_000, 1.0,
                                                  TimeSpan.FromMinutes(4), advancing: false);
        Assert.Equal(42, projected.TotalSeconds, 3);
    }

    [Fact]
    public void Position_is_clamped_to_just_before_duration()
    {
        var projected = MediaRules.ProjectPosition(TimeSpan.FromSeconds(230), anchorAtMs: 1, nowMs: 600_000, rate: 1.0,
                                                  TimeSpan.FromMinutes(4), advancing: true);
        Assert.True(projected < TimeSpan.FromMinutes(4));
        Assert.Equal(239, projected.TotalSeconds, 0);
    }

    [Fact]
    public void Position_never_negative()
    {
        var projected = MediaRules.ProjectPosition(TimeSpan.FromSeconds(1), 5000, 1000, 1.0,
                                                  TimeSpan.Zero, advancing: true);
        Assert.True(projected >= TimeSpan.Zero);
    }

    [Fact]
    public void Unknown_position_until_song_change_or_timeline_or_resume()
    {
        Assert.False(MediaRules.IsPositionKnown(timelineReal: false, resumedFromDisk: false, sawSongChange: false));
        Assert.True(MediaRules.IsPositionKnown(false, false, true));
        Assert.True(MediaRules.IsPositionKnown(false, true, false));
        Assert.True(MediaRules.IsPositionKnown(true, false, false));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("干杯", true)]
    public void Empty_title_keeps_previous_metadata(string? title, bool expected)
        => Assert.Equal(expected, MediaRules.ShouldApplyMetadata(title));

    [Fact]
    public void Source_is_dead_when_process_missing()
    {
        Assert.False(MediaRules.IsSourceAlive("cloudmusic.exe", _ => false));
        Assert.True(MediaRules.IsSourceAlive("cloudmusic.exe", _ => true));
    }

    [Fact]
    public void Packaged_app_id_is_treated_as_alive()
        => Assert.True(MediaRules.IsSourceAlive("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", _ => false));

    [Fact]
    public void App_id_with_path_is_normalized()
    {
        string? asked = null;
        _ = MediaRules.IsSourceAlive(@"C:\Program Files\App\player.exe", name => { asked = name; return true; });
        Assert.Equal("player", asked);
    }
}
