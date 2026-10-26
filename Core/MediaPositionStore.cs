using System.Runtime.InteropServices;

namespace MacDock.Core;

/// <summary>
/// 网易云音乐这类"不对外报播放位置"的播放器，我们只能本地推算。
/// 这个类解决最疼的一种情况：**MacDock 自己重启后位置归零**。
/// 做法是把"歌名 + 已播秒数 + 保存时刻"写到磁盘，下次启动若还是同一首、
/// 且间隔不久，就把锚点恢复成"上次的位置 + 间隔"，而不是从 0 开始。
/// </summary>
internal static class MediaPositionStore
{
    sealed class Entry
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public double PositionSeconds { get; set; }
        public long SavedAtUnix { get; set; }
        public bool Playing { get; set; }
    }

    static string FilePath => Path.Combine(DockConfig.Folder, "media-state.json");
    static readonly object Gate = new();

    /// <summary>恢复上次的播放位置（不匹配或太旧则返回 null）。</summary>
    public static TimeSpan? TryResume(string title, string artist, TimeSpan duration)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var entry = System.Text.Json.JsonSerializer.Deserialize<Entry>(File.ReadAllText(FilePath));
            if (entry is null) return null;
            if (!string.Equals(entry.Title, title, StringComparison.Ordinal)) return null;
            if (!string.IsNullOrWhiteSpace(artist) && !string.Equals(entry.Artist, artist, StringComparison.Ordinal)) return null;

            var elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(entry.SavedAtUnix);
            if (elapsed < TimeSpan.Zero || elapsed > TimeSpan.FromHours(4)) return null;   // 太久，不靠谱

            double seconds = entry.PositionSeconds + (entry.Playing ? elapsed.TotalSeconds : 0);
            if (seconds < 0) return null;
            if (duration > TimeSpan.Zero && seconds > duration.TotalSeconds) return null;  // 已经放完了
            return TimeSpan.FromSeconds(seconds);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string title, string artist, TimeSpan position, bool playing)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            var entry = new Entry
            {
                Title = title,
                Artist = artist,
                PositionSeconds = position.TotalSeconds,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Playing = playing,
            };
            lock (Gate)
            {
                Directory.CreateDirectory(DockConfig.Folder);
                File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(entry));
            }
        }
        catch (Exception)
        {
            // 位置持久化失败不影响播放显示
        }
    }
}
