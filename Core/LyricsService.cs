using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace MacDock.Core;

/// <summary>
/// 歌词：从网易云音乐的公开接口取（搜索拿 id → 歌词接口拿 LRC），按"歌名 + 歌手"缓存。
///
/// 为什么走公开接口而不是逆向客户端：
/// 客户端歌词渲染在它自己的 CEF 窗口（OrpheusBrowserHost）里，要取出来得挂进去改它的进程；
/// 公开接口同样拿得到逐行 LRC（含翻译），而且只读、无登录、失败无副作用。
///
/// 网络只在**换歌时**发生一次，之后落盘缓存，播放过程中零请求。
/// </summary>
internal static class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly Dictionary<string, Lyrics?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return client;
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

    /// <summary>取歌词（命中缓存就不发请求）。失败返回 null，不抛。</summary>
    public static async Task<Lyrics?> GetAsync(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string key = title.Trim() + "|" + artist.Trim();
        lock (Gate) if (Cache.TryGetValue(key, out var cached)) return cached;

        Lyrics? result = null;
        try
        {
            var file = Path.Combine(DockConfig.Folder, "lyrics", Sanitize(key) + ".lrc");
            if (File.Exists(file))
            {
                result = Parse(await File.ReadAllTextAsync(file));
            }
            else
            {
                long id = await SearchSongIdAsync(title, artist);
                if (id > 0)
                {
                    string lrc = await GetLyricAsync(id);
                    result = Parse(lrc);
                    if (result.Lines.Count > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await File.WriteAllTextAsync(file, lrc);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fetching lyrics");
        }

        lock (Gate) Cache[key] = result;
        return result;
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

    static async Task<string> GetLyricAsync(long id)
    {
        using var response = await Http.GetAsync($"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1");
        if (!response.IsSuccessStatusCode) return "";
        string text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        if (!json.RootElement.TryGetProperty("lrc", out var lrc)) return "";
        return lrc.TryGetProperty("lyric", out var lyric) ? lyric.GetString() ?? "" : "";
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
            // 逐行时间戳可能一行多个，这里只取第一个
            string[] parts = stamp.Split(':', '.');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out int minutes)) continue;
            if (!int.TryParse(parts[1], out int seconds)) continue;
            if (!int.TryParse(parts[2].PadRight(3, '0')[..3], out int millis)) continue;
            string text = line[(close + 1)..].Trim();
            if (text.Length == 0) continue;
            lyrics.Lines.Add((new TimeSpan(0, 0, minutes, seconds, millis), text));
        }
        lyrics.Lines.Sort((a, b) => a.At.CompareTo(b.At));
        return lyrics;
    }

    /// <summary>过滤 LRC 头部的制作人员信息行（它们带时间戳但会占满卡片的那一行）。</summary>
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
