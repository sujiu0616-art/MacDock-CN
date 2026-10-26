using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MacDock.Core;

/// <summary>
/// 歌曲元数据：歌词（逐行 LRC）+ 准确时长。
///
/// 为什么走网易云公开接口而不是逆向客户端：
/// 客户端把歌词渲染在自己的 CEF 窗口（OrpheusBrowserHost）里，取出来要注入它的进程；
/// 而它注册的 SMTC 时间轴**恒为 0**（实测 start=end=pos=00:00:00），时长也只能从接口拿。
/// 公开接口只读、无登录，失败无副作用。
///
/// 网络只在**换歌时**发生一次（搜索 → 歌词 + 详情），之后落盘缓存，播放过程中零请求。
/// </summary>
internal static class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Dictionary<string, SongInfo?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
    }

    /// <summary>一首歌的歌词 + 准确时长（Duration 为 0 表示没取到）。</summary>
    internal sealed class SongInfo
    {
        public Lyrics? Lyrics { get; init; }
        public TimeSpan Duration { get; init; }
    }

    internal sealed class Lyrics
    {
        public readonly List<(TimeSpan At, string Text)> Lines = new();

        public string LineAt(TimeSpan position)
        {
            if (Lines.Count == 0) return "";
            int low = 0, high = Lines.Count - 1, answer = -1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (Lines[mid].At <= position) { answer = mid; low = mid + 1; }
                else high = mid - 1;
            }
            return answer < 0 ? "" : Lines[answer].Text;
        }
    }

    /// <summary>取歌词与时长（命中缓存就不发请求）。失败返回 null，不抛。</summary>
    public static async Task<SongInfo?> GetAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string key = title.Trim() + "|" + artist.Trim();
        lock (Gate) if (Cache.TryGetValue(key, out var cached)) return cached;

        SongInfo? info = null;
        try
        {
            var folder = Path.Combine(DockConfig.Folder, "lyrics");
            var lrcFile = Path.Combine(folder, Sanitize(key) + ".lrc");
            var metaFile = Path.Combine(folder, Sanitize(key) + ".meta");

            if (File.Exists(lrcFile) && File.Exists(metaFile))
            {
                long ticks = 0;
                _ = long.TryParse((await File.ReadAllTextAsync(metaFile)).Trim(), out ticks);
                var cachedLyrics = Parse(await File.ReadAllTextAsync(lrcFile));
                info = new SongInfo { Lyrics = cachedLyrics, Duration = TimeSpan.FromTicks(Math.Max(0, ticks)) };
            }
            else
            {
                long id = await SearchSongIdAsync(title, artist);
                if (id > 0)
                {
                    var (lrcText, duration) = await FetchLyricAndDurationAsync(id);
                    var lyrics = Parse(lrcText);
                    info = new SongInfo { Lyrics = lyrics, Duration = duration };

                    if (lyrics.Lines.Count > 0 || duration > TimeSpan.Zero)
                    {
                        Directory.CreateDirectory(folder);
                        if (lyrics.Lines.Count > 0) await File.WriteAllTextAsync(lrcFile, lrcText);
                        await File.WriteAllTextAsync(metaFile, duration.Ticks.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fetching lyrics");
        }

        lock (Gate) Cache[key] = info;
        return info;
    }

    static async Task<long> SearchSongIdAsync(string title, string artist)
    {
        string query = Uri.EscapeDataString($"{title} {artist}".Trim());
        string url = $"https://music.163.com/api/search/get/web?s={query}&type=1&offset=0&limit=5";
        using var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode) return 0;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        if (!json.RootElement.TryGetProperty("result", out var result)) return 0;
        if (!result.TryGetProperty("songs", out var songs) || songs.GetArrayLength() == 0) return 0;

        long fallback = 0;
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idProperty)) continue;
            long id = idProperty.GetInt64();
            if (fallback == 0) fallback = id;
            if (!song.TryGetProperty("name", out var name)) continue;
            string nameText = name.GetString() ?? "";
            if (!nameText.Contains(title, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(artist) && song.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
            {
                string firstName = artists[0].TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!firstName.Contains(artist, StringComparison.OrdinalIgnoreCase) && !artist.Contains(firstName, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            return id;
        }
        return fallback;
    }

    static async Task<(string Lyric, TimeSpan Duration)> FetchLyricAndDurationAsync(long id)
    {
        string lrcText = "";
        using (var response = await Http.GetAsync($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1"))
        {
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (json.RootElement.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var lyric))
                    lrcText = lyric.GetString() ?? "";
            }
        }

        TimeSpan duration = TimeSpan.Zero;
        try
        {
            using var detail = await Http.GetAsync($"https://music.163.com/api/song/detail?ids=[{id}]");
            if (detail.IsSuccessStatusCode)
            {
                using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
                if (detailJson.RootElement.TryGetProperty("songs", out var songs) && songs.GetArrayLength() > 0
                    && songs[0].TryGetProperty("duration", out var ms) && ms.TryGetInt64(out long millis) && millis > 0)
                    duration = TimeSpan.FromMilliseconds(millis);
            }
        }
        catch (Exception) { }

        return (lrcText, duration);
    }

    static Lyrics Parse(string lrc)
    {
        var lyrics = new Lyrics();
        if (string.IsNullOrWhiteSpace(lrc)) return lyrics;
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 5 || line[0] != '[') continue;
            int close = line.IndexOf(']');
            if (close < 0) continue;
            string stamp = line[1..close];
            string[] parts = stamp.Split(':', '.');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out int minutes)) continue;
            if (!int.TryParse(parts[1], out int seconds)) continue;
            if (!int.TryParse(parts[2].PadRight(3, '0')[..3], out int millis)) continue;
            string text = line[(close + 1)..].Trim();
            if (text.Length == 0) continue;
            if (IsMetadataLine(text)) continue;   // 作词/作曲/音乐发行 这些不是歌词
            lyrics.Lines.Add((new TimeSpan(0, 0, minutes, seconds, millis), text));
        }
        lyrics.Lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lyrics;
    }

    /// <summary>过滤 LRC 头部的制作人员信息行（它们带时间戳但会占满卡片那一行）。</summary>
    static bool IsMetadataLine(string text)
    {
        ReadOnlySpan<string> prefixes = new[]
        {
            "作词", "作曲", "编曲", "制作人", "音乐发行", "发行", "版权", "出品", "监制",
            "混音", "母带", "录音", "和声", "吉他", "贝斯", "鼓", "键盘", "弦乐", "OP", "SP",
            "Lyrics", "Composer", "Arranger", "Producer", "Mixed", "Mastered",
        };
        foreach (string prefix in prefixes)
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return text.Contains(" : ", StringComparison.Ordinal) && text.Length < 40;
    }

    static string Sanitize(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return value.Length > 80 ? value[..80] : value;
    }
}
