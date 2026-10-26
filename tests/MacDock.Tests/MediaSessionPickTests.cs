using MacDock.Core;

namespace MacDock.Tests;

/// <summary>多会话优先级：正在播放优先；同等状态下取时间轴更新更晚的。</summary>
public class MediaSessionPickTests
{
    static MediaRules.SessionSnapshot Snap(string appId, int status, long updated = 0)
        => new(appId, status, updated);

    [Fact]
    public void Most_recent_playing_session_wins()
    {
        var sessions = new[]
        {
            Snap("cloudmusic.exe", MediaRules.StatusPlaying, updated: 100),
            Snap("msedge.exe", MediaRules.StatusPlaying, updated: 900),
        };
        Assert.Equal(1, MediaRules.PickBestIndex(sessions));
    }

    [Fact]
    public void Playing_beats_more_recent_paused()
    {
        var sessions = new[]
        {
            Snap("cloudmusic.exe", MediaRules.StatusPlaying, updated: 100),
            Snap("msedge.exe", MediaRules.StatusPaused, updated: 9_999_999),
        };
        Assert.Equal(0, MediaRules.PickBestIndex(sessions));
    }

    [Fact]
    public void Single_session_is_selected()
        => Assert.Equal(0, MediaRules.PickBestIndex(new[] { Snap("cloudmusic.exe", MediaRules.StatusPaused) }));
}
