using System.Windows.Media;

namespace MacDock.Core;

/// <summary>
/// 启动台里的一个应用：开始菜单里的那条快捷方式、它的图标、以及搜索要比对的几串文字。
/// 名字、去标点的小写名和首字母串都在构造时算好，输入搜索时就不用每敲一个字重新算拼音。
/// </summary>
internal sealed class LaunchpadEntry
{
    public LaunchpadEntry(string name, string shortcut)
    {
        Name = name;
        Shortcut = shortcut;
        Lowered = name.ToLowerInvariant();
        Letters = KeepLetters(Lowered);
        Initials = PinyinIndex.Initials(name);
    }

    /// <summary>图标下面显示的名字（快捷方式去掉 .lnk 的文件名，开始菜单也是这么显示的）。</summary>
    public string Name { get; }

    /// <summary>磁盘上的 .lnk：启动、"在文件资源管理器中显示"都用它。</summary>
    public string Shortcut { get; }

    /// <summary>小写后的名称，用于"名称包含"。</summary>
    public string Lowered { get; }

    /// <summary>去掉空格标点的小写名称，用于子序列匹配（"vsc" → Visual Studio Code）。</summary>
    public string Letters { get; }

    /// <summary>拼音首字母 / 拉丁词首字母（"wx" → 微信）。</summary>
    public string Initials { get; }

    /// <summary>256 px 图标；后台线程填，填好之前是 null。</summary>
    public ImageSource? Icon { get; set; }

    /// <summary>
    /// 搜索是否命中。三条路子，任一命中即可：名称包含、名称子序列、首字母（前缀优先，也允许出现在中间，
    /// 这样 "yyy" 也能找到「网易云音乐」）。
    /// </summary>
    public bool Matches(string query)
    {
        if (query.Length == 0) return true;
        var needle = query.ToLowerInvariant();
        if (Lowered.Contains(needle, StringComparison.Ordinal)) return true;
        if (Subsequence(Letters, needle)) return true;
        return Initials.StartsWith(needle, StringComparison.Ordinal)
               || Initials.Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>needle 的字符是否按顺序出现在 text 里（不必连续）。</summary>
    static bool Subsequence(string text, string needle)
    {
        int at = 0;
        foreach (char c in text)
        {
            if (at < needle.Length && c == needle[at]) at++;
            if (at == needle.Length) return true;
        }
        return false;
    }

    static string KeepLetters(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) text.Append(c);
        return text.ToString();
    }
}

/// <summary>
/// 启动台的内容：把用户和公共开始菜单里的 .lnk 收全、去重、按名称排序，然后在后台线程逐个解出图标。
/// 扫描和取图标都可能碰到坏快捷方式，出错只记日志、跳过那一条，绝不让启动台打不开。
/// </summary>
internal static class LaunchpadModel
{
    public const int Columns = 7;
    public const int Rows = 4;
    public const int PageSize = Columns * Rows;

    /// <summary>开始菜单里这些东西不是应用，摆进启动台只会碍眼（卸载程序、帮助、官网链接……）。</summary>
    static readonly string[] NoiseWords =
    [
        "uninstall", "卸载", "readme", "read me", "help", "帮助", "website", "官方网站", "official site",
        "repair", "修复", "documentation", "changelog", "release notes", "许可协议", "license agreement",
    ];

    /// <summary>用户开始菜单在前、公共开始菜单在后：同名时用户自己那份说了算。</summary>
    static IEnumerable<string> Folders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    /// <summary>
    /// 收集开始菜单里的应用。只收 .lnk（.url 是网页收藏，不是应用），跳过"启动"文件夹和卸载一类噪音，
    /// 按名称去重再按名称排序——排队顺序必须是确定的，分页才不会在两台机器上乱跳。
    /// </summary>
    public static List<LaunchpadEntry> Scan()
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var entries = new List<LaunchpadEntry>();

        foreach (var root in Folders())
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (var file in Shortcuts(root))
            {
                if (IsInStartup(file, root)) continue;
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                if (name.Length == 0 || IsNoise(name)) continue;
                if (!seen.Add(name)) continue;
                entries.Add(new LaunchpadEntry(name, file));
            }
        }


        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Log.Info($"Launchpad: {entries.Count} apps from the Start menu");
        return entries;
    }

    /// <summary>
    /// 自己逐层走目录。开始菜单里真有读不了的文件夹（这台机器上就有一个 "程序"），而
    /// <c>EnumerateFiles(AllDirectories)</c> 碰到一个就整体抛异常，会把后面所有快捷方式一起丢掉；
    /// 这里坏一个文件夹只记一条日志，接着走其余的。
    /// </summary>
    static IEnumerable<string> Shortcuts(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder, "*.lnk");
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Reading the Start menu at {folder}");
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var sub in folders) pending.Push(sub);
        }
    }

    /// <summary>
    /// 逐个解图标（每条要过一遍 Shell 的 COM 调用，一百多条要一两秒，所以只能放后台线程）。
    /// 每解出一个就回调一次，界面可以先把名字摆上去、图标慢慢填，不用等全部完事。
    /// </summary>
    public static void LoadIcons(IReadOnlyList<LaunchpadEntry> entries, Action<LaunchpadEntry> loaded, CancellationToken token)
    {
        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                entry.Icon = ShellItems.Icon(entry.Shortcut);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Launchpad icon of {entry.Shortcut}");
            }
            if (token.IsCancellationRequested) return;
            loaded(entry);
        }
        Log.Info($"Launchpad: icons loaded for {entries.Count} apps");
    }

    /// <summary>启动：交给 Launcher（它负责 shell: 路径、出错记日志），和 Dock 上的图标走同一条路。</summary>
    public static void Launch(LaunchpadEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Shortcut)) return;
        try
        {
            Launcher.Launch(new DockItem(new DockItemConfig { Name = entry.Name, Target = entry.Shortcut }));
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Launching {entry.Shortcut}");
        }
    }

    /// <summary>在文件资源管理器中选中那条快捷方式。</summary>
    public static void Reveal(LaunchpadEntry entry)
    {
        try
        {
            var file = Environment.ExpandEnvironmentVariables(entry.Shortcut);
            var argument = File.Exists(file) ? $"/select,\"{file}\"" : $"\"{file}\"";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Showing {entry.Shortcut} in Explorer");
        }
    }

    static bool IsInStartup(string file, string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, file);
        var folder = System.IO.Path.GetDirectoryName(relative);
        if (folder is null) return false;
        return folder.Contains("Startup", StringComparison.OrdinalIgnoreCase) || folder.Contains("启动", StringComparison.Ordinal);
    }

    /// <summary>拉丁词按整词比（"helper" 不该被 "help" 误杀），中文词直接包含比。</summary>
    static bool IsNoise(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var word in NoiseWords)
        {
            if (word.Length == 0) continue;
            if (word[0] < 0x80)
            {
                if (lower == word) return true;
                // "ChangeLogRus" / "Readme(中文)" 这类也一并挡掉，同理不能误杀 Helper 之类
                if (lower.StartsWith(word, StringComparison.Ordinal) && !char.IsLetter(lower[word.Length])) return true;
                if (lower.StartsWith(word + " ", StringComparison.Ordinal) || lower.Contains(" " + word, StringComparison.Ordinal)) return true;
            }
            else if (lower.Contains(word, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
