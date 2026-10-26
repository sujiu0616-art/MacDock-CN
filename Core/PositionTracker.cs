using System;

namespace MacDock.Core;

/// <summary>
/// 播放位置的推进器（纯逻辑，便于单测）：锚点 + 速率外推、暂停不外推、
/// 时长封顶、单曲循环贴顶 1.5 秒后自动归零。
/// MediaWatcher 只负责喂输入（状态/时长/时间轴），判断都走这里。
/// </summary>
public sealed class PositionTracker
{
    public TimeSpan Anchor { get; private set; }
    public long AnchorAtMs { get; private set; }
    public double Rate { get; private set; } = 1.0;
    public TimeSpan Duration { get; private set; }
    public bool Known { get; private set; }
    public bool Advancing { get; private set; }
    public int LoopRewinds { get; private set; }

    long _ceilingSinceMs;

    /// <summary>当前位置；位置未知时不做外推（UI 会显示 --:--）。</summary>
    public TimeSpan Current(long nowMs)
        => Known
            ? MediaRules.ProjectPosition(Anchor, AnchorAtMs, nowMs, Rate, Duration, Advancing)
            : Anchor;

    /// <summary>换歌 / 从磁盘恢复 / 时间轴真值：重置锚点。</summary>
    public void Reanchor(TimeSpan position, long nowMs, TimeSpan duration, bool known)
    {
        Anchor = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        AnchorAtMs = nowMs;
        Duration = duration;
        Known = known;
        _ceilingSinceMs = 0;
    }

    public void SetPlayback(bool advancing, double rate, TimeSpan duration, long nowMs)
    {
        // 切换前先把当前推算值落成锚点，避免暂停/继续丢时间
        Anchor = Current(nowMs);
        AnchorAtMs = nowMs;
        Advancing = advancing;
        Rate = rate > 0.01 ? rate : 1.0;
        if (duration > TimeSpan.Zero) Duration = duration;
    }

    public void SetDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero) Duration = duration;
    }

    /// <summary>
    /// 每秒调用一次：位置贴到"时长-1s"且仍在播放超过 1.5 秒 → 判定单曲循环重启，锚点归零。
    /// 返回 true 表示刚刚发生了回绕。
    /// </summary>
    public bool Tick(long nowMs, int loopThresholdMs = 1500)
    {
        if (!Known || Duration <= TimeSpan.Zero || !Advancing) { _ceilingSinceMs = 0; return false; }
        var position = Current(nowMs);
        if (!MediaRules.IsAtCeiling(position, Duration)) { _ceilingSinceMs = 0; return false; }

        if (_ceilingSinceMs == 0) { _ceilingSinceMs = nowMs; return false; }
        if (!MediaRules.ShouldRestartLoop(_ceilingSinceMs, nowMs, loopThresholdMs)) return false;

        Anchor = TimeSpan.Zero;
        AnchorAtMs = nowMs;
        _ceilingSinceMs = 0;
        LoopRewinds++;
        return true;
    }
}
