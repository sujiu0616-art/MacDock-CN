using System.Runtime.InteropServices;
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
/// 启动台的内容：把用户和公共开始菜单里的 .lnk 收全、去噪、去重、按名称排序，然后在后台线程逐个解出图标。
///
/// 只收"真的是个应用"的条目：开一条快捷方式要能解析出目标，目标得是 .exe、不能是文件夹、不能是
/// 帮助/文档/安装包/许可证一类。这台机器的开始菜单有 168 条 .lnk，其中一大半是 WSL 发行版带的应用
/// （"Xfce Terminal (Ubuntu-24.04)" 这种）、卸载器、MATLAB 的激活工具和文件夹快捷方式，
/// 全塞进启动台就没法看了 —— 过滤后剩几十条真应用。
/// </summary>
internal static class LaunchpadModel
{
    public const int Columns = 7;
    public const int Rows = 4;
    public const int PageSize = Columns * Rows;

    /// <summary>名字里带这些词的条目不是应用（卸载、帮助、官网、许可证、更新器……）。</summary>
    static readonly string[] NoiseWords =
    [
        "uninstall", "卸载", "readme", "read me", "help", "帮助", "website", "官方网站", "official site",
        "repair", "修复", "documentation", "changelog", "release notes", "许可协议", "license agreement",
        "activate", "deactivate", "激活", "反激活", "updater", "更新程序",
        // 目标是空的 shell 项与安装型助手：解析不出 exe，只能按名字挡
        "file explorer", "control panel", "administrative tools", "run", "runonce",
        "distiller", "桌面", "此电脑", "打开日志目录",
    ];

    /// <summary>目标文件是这些后缀时不是应用（帮助文档、网页、文档、安装包、系统管理单元）。</summary>
    static readonly string[] NoiseExtensions =
    [
        ".chm", ".hlp", ".txt", ".rtf", ".html", ".htm", ".url", ".pdf", ".msc", ".cpl", ".ico", ".jpg", ".png",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    ];

    /// <summary>WSL 发行版给开始菜单塞的应用（"Thunar (Ubuntu-24.04)"）：留着只会把启动台淹掉。</summary>
    static readonly System.Text.RegularExpressions.Regex DistroSuffix = new(
        @"\s*\((?:ubuntu|debian|kali|fedora|arch|manjaro|opensuse|alpine|almalinux|rocky)[^)]*\)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>用户开始菜单在前、公共开始菜单在后：同名时用户自己那份说了算。</summary>
    static IEnumerable<string> Folders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
    }

    // ── 扫描结果缓存 ─────────────────────────────────────────────────────
    // 上次扫出来的 (Name, Shortcut) 存成 JSON；启动台先用它秒显，后台再核对一遍。
    static string CachePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock", "launchpad-cache.json");

    sealed record CachedEntry(string Name, string Shortcut);

    /// <summary>读缓存（可能为 null）。几十毫秒级，可以直接在 UI 线程调用。</summary>
    public static List<LaunchpadEntry>? LoadCached()
    {
        try
        {
            if (!System.IO.File.Exists(CachePath)) return null;
            var rows = System.Text.Json.JsonSerializer.Deserialize<List<CachedEntry>>(System.IO.File.ReadAllText(CachePath));
            if (rows is null || rows.Count == 0) return null;
            var list = new List<LaunchpadEntry>(rows.Count);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Shortcut) || string.IsNullOrEmpty(row.Name)) continue;
                if (!System.IO.File.Exists(row.Shortcut)) continue;   // 卸载掉的程序不留
                list.Add(new LaunchpadEntry(row.Name, row.Shortcut));
            }
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the Launchpad cache");
            return null;
        }
    }

    /// <summary>把扫描结果写回缓存（后台线程调用）。</summary>
    public static void SaveCache(IReadOnlyList<LaunchpadEntry> entries)
    {
        try
        {
            var rows = entries.Select(e => new CachedEntry(e.Name, e.Shortcut)).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(rows);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CachePath)!);
            System.IO.File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Writing the Launchpad cache");
        }
    }

    public static List<LaunchpadEntry> Scan()
    {
        var byTarget = new Dictionary<string, LaunchpadEntry>(StringComparer.OrdinalIgnoreCase);
        int considered = 0, skippedDistro = 0, skippedNoise = 0, skippedNotApp = 0, skippedSystem = 0, merged = 0;
        using var targets = new ShortcutTargets();

        foreach (var root in Folders())
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (var file in Shortcuts(root))
            {
                if (IsInStartup(file, root)) continue;
                var name = Clean(System.IO.Path.GetFileNameWithoutExtension(file));
                if (name.Length == 0) continue;
                considered++;

                if (DistroSuffix.IsMatch(name)) { skippedDistro++; continue; }
                if (IsNoise(name)) { skippedNoise++; continue; }

                var target = targets.Target(file);
                if (!IsApplication(target)) { skippedNotApp++; continue; }
                if (IsSystemTool(target)) { skippedSystem++; continue; }

                // 同一个程序在开始菜单里常有好几条（微信两份、Gmail 走 chrome_proxy……）：按目标合并。
                // 解析不出目标（UWP 的 shell: 条目）时用快捷方式路径当键，别把条目丢掉。
                var key = string.IsNullOrWhiteSpace(target) ? "lnk:" + file : target;
                if (byTarget.TryGetValue(key, out var existing))
                {
                    merged++;
                    // 名字取更短的那条："微信" 胜过 "微信 (64-bit)"。
                    if (name.Length < existing.Name.Length) byTarget[key] = new LaunchpadEntry(name, file);
                    continue;
                }
                byTarget[key] = new LaunchpadEntry(name, file);
            }
        }

        var entries = byTarget.Values.ToList();
        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Log.Info($"Launchpad: {entries.Count} apps kept of {considered} shortcuts " +
                 $"(distro {skippedDistro}, noise {skippedNoise}, not-an-app {skippedNotApp}, system {skippedSystem}, merged {merged})");
        return entries;
    }

    /// <summary>
    /// 目标是否"是个应用"：必须解析得出来、是文件、不是文件夹、不是文档/安装包一类。
    /// 解析不出来（坏快捷方式、UWP 的 shell: 目标）时按名字放行 —— 宁可多留一条也不要漏掉真应用。
    /// </summary>
    static bool IsApplication(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return true;
        try
        {
            if (Directory.Exists(target)) return false;
            var extension = System.IO.Path.GetExtension(target);
            if (extension.Length > 0 && NoiseExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
            // 安装器的图标壳（Windows\Installer\{GUID}\foo.ico）不是应用
            if (target.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Checking the shortcut target {target}");
            return true;
        }
    }

    /// <summary>系统自带工具的文件名：它们也出现在开始菜单里，但不是"应用"。</summary>
    static readonly string[] SystemTools =
    [
        "explorer.exe",            // "打开日志目录"/"桌面" 这类快捷方式其实都是它
        "cmd.exe", "control.exe", "charmap.exe", "dfrgui.exe", "cleanmgr.exe", "regedit.exe",
        "taskmgr.exe", "perfmon.exe", "msinfo32.exe", "msconfig.exe", "iscsicpl.exe", "mdsched.exe",
        "odbcad32.exe", "recoverydrive.exe", "narrator.exe", "magnify.exe", "osk.exe",
        "voiceaccess.exe", "livecaptions.exe", "psr.exe", "wslg.exe", "systemsettings.exe",
        "mmc.exe", "eventvwr.exe", "resmon.exe",
    ];

    /// <summary>
    /// 目标是不是 Windows 自带的工具：%WINDIR% 下的东西几乎都是（命令提示符、控制面板、磁盘整理、
    /// 事件查看器、辅助功能…），真应用不会装在那里；再加上一份文件名名单兜住 Program Files 里的系统件。
    /// </summary>
    static bool IsSystemTool(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;      // 未知目标：按应用放行
        var file = System.IO.Path.GetFileName(target);
        if (file.Length > 0 && SystemTools.Contains(file, StringComparer.OrdinalIgnoreCase)) return true;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(windows) && target.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 自己逐层走目录。开始菜单里真有读不了的文件夹（这台机器上就有一个 "程序"），而
    /// <c>EnumerateFiles(AllDirectories)</c> 碰到一个就整体抛异常，会把后面所有快捷方式一起丢掉；
    /// 这里坏一个文件夹只记一条日志，接着走其余的。
    /// </summary>
    /// <summary>已经记过日志的"拒绝访问"目录（系统目录，一次就够）。</summary>
    static readonly HashSet<string> _deniedLogged = new(StringComparer.OrdinalIgnoreCase);

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
                // Windows 在开始菜单里放了一批"旧式本地化"目录（如 "程序"），它们是 Hidden+System 的
                // ReparsePoint，枚举一律被拒；跳过去既不丢应用（内容在 Programs 里），也省掉每次扫描的异常。
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;

                files = Directory.GetFiles(folder, "*.lnk");
                folders = Directory.GetDirectories(folder);
            }
            catch (UnauthorizedAccessException)
            {
                // 只记一次：这是系统目录的固定行为，不是故障
                if (_deniedLogged.Add(folder)) Log.Info($"Start menu folder skipped (access denied): {folder}");
                continue;
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
    /// 逐个解图标（每条要过一遍 Shell 的 COM 调用，几十上百条要一两秒，所以只能放后台线程）。
    /// 每解出一个就回调一次，界面可以先把名字摆上去、图标慢慢填，不用等全部完事。
    /// </summary>
    public static void LoadIcons(IReadOnlyList<LaunchpadEntry> entries, Action<LaunchpadEntry> loaded, CancellationToken token)
    {
        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested) return;
            try
            {
                entry.Icon = IconArt.Squircle(ShellItems.Icon(entry.Shortcut), 128);
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

    /// <summary>去掉开始菜单里那些"给人看但占地方"的尾巴：位数、" - 快捷方式"、安装包后缀。</summary>
    static string Clean(string name)
    {
        var text = name.Trim();
        foreach (var suffix in new[] { " (64-bit)", " (32-bit)", " - Shortcut", " - 快捷方式", " (快捷方式)" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length].Trim();
        }
        return text;
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

    /// <summary>
    /// 解快捷方式的目标路径。用一次 WScript.Shell 的后期绑定调用，比每条都起一个 COM 对象快得多；
    /// 失败就返回 null（调用方按"未知目标"处理，不会因为一条坏链接丢掉整份清单）。
    /// </summary>
    sealed class ShortcutTargets : IDisposable
    {
        readonly object? _shell;

        public ShortcutTargets()
        {
            try
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type is not null) _shell = Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Creating the shortcut reader");
            }
        }

        public string? Target(string shortcut)
        {
            if (_shell is null) return null;
            try
            {
                var link = _shell.GetType().InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, _shell, [shortcut]);
                if (link is null) return null;
                var target = link.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, link, null) as string;
                return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Reading the shortcut {shortcut}");
                return null;
            }
        }

        public void Dispose()
        {
            if (_shell is not null && Marshal.IsComObject(_shell)) Marshal.FinalReleaseComObject(_shell);
        }
    }
}
