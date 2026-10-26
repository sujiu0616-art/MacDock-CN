using System;

namespace MacDock.Core;

/// <summary>
/// 媒体相关的纯规则（无 WinRT 依赖，便于单测）。
/// MediaWatcher 只负责"取数据"，判断逻辑集中在这里。
/// </summary>
public static class MediaRules
{
    /// <summary>会话候选（对应 SMTC 的一个会话）。</summary>
    public readonly record struct SessionSnapshot(string AppId, int Status, long LastUpdatedUnixMs);

    // SMTC PlaybackStatus 数值：0 Closed, 1 Opened, 2 Changing, 3 Stopped, 4 Playing, 5 Paused
    public const int StatusPlaying = 4;
    public const int StatusPaused = 5;
    public const int StatusChanging = 2;
    public const int StatusStopped = 3;
    public const int StatusClosed = 0;

    /// <summary>
    /// 多个会话并存时的优先级：正在播放 &gt; 最近活跃 &gt; 第一个。
    /// 这样网易云和浏览器同时有会话时，不会因为浏览器抢了 CurrentSession 就把岛切走。
    /// </summary>
    public static int PickBestIndex(ReadOnlySpan<SessionSnapshot> sessions)
    {
        if (sessions.Length == 0) return -1;

        for (int i = 0; i < sessions.Length; i++)
            if (sessions[i].Status == StatusPlaying) return i;

        int best = 0;
        for (int i = 1; i < sessions.Length; i++)
            if (sessions[i].LastUpdatedUnixMs > sessions[best].LastUpdatedUnixMs) best = i;
        return best;
    }

    /// <summary>SMTC 状态 → 四态（播放/暂停/缓冲/停止）。</summary>
    public static string DescribeState(int status) => status switch
    {
        StatusPlaying => "Playing",
        StatusPaused => "Paused",
        StatusChanging => "Buffering",
        _ => "Stopped",
    };

    /// <summary>
    /// 位置外推：锚点位置 + (经过时间 × 速率)，并按时长封顶。
    /// 暂停/停止时不外推；时长未知时只保证不为负。
    /// </summary>
    public static TimeSpan ProjectPosition(TimeSpan anchor, long anchorAtMs, long nowMs, double rate,
                                           TimeSpan duration, bool advancing)
    {
        if (anchorAtMs == 0 || !advancing) return Clamp(anchor, duration);
        if (rate <= 0.01) rate = 1.0;
        double driftMs = (nowMs - anchorAtMs) * rate;
        if (driftMs <= 0) return Clamp(anchor, duration);
        var projected = anchor + TimeSpan.FromMilliseconds(driftMs);
        return Clamp(projected, duration);
    }

    static TimeSpan Clamp(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (duration <= TimeSpan.Zero) return value;
        var ceiling = duration > TimeSpan.FromSeconds(2) ? duration - TimeSpan.FromSeconds(1) : duration;
        return value > ceiling ? ceiling : value;
    }

    /// <summary>
    /// 位置是否可信：SMTC 时间轴为真、有落盘续算、或我们亲眼看到过换歌。
    /// 首次挂上（可能已经播到一半）时为 false，UI 应显示 --:-- 而不是瞎给数字。
    /// </summary>
    public static bool IsPositionKnown(bool timelineReal, bool resumedFromDisk, bool sawSongChange)
        => timelineReal || resumedFromDisk || sawSongChange;

    /// <summary>
    /// 元数据是否可用：标题为空（加载中 / VIP 试听 / 版权受限）时保留上一次的完整信息，
    /// 避免出现"新歌名 + 旧歌手"的中间态。
    /// </summary>
    public static bool ShouldApplyMetadata(string? title) => !string.IsNullOrWhiteSpace(title);

    /// <summary>
    /// 源进程是否还活着：SMTC 会话在应用退出后会残留一段时间，
    /// 这里按 AUMID 里的 exe 名判断（UWP 包名无法判断时放行）。
    /// </summary>
    public static bool IsSourceAlive(string? appId, Func<string, bool> processExists)
    {
        if (string.IsNullOrWhiteSpace(appId)) return true;
        string name = appId;
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0 && slash + 1 < name.Length) name = name[(slash + 1)..];
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        try { return processExists(name[..^4]); }
        catch (Exception) { return true; }
    }
}
