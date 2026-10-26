using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacDock.Core;

internal enum DockEdge { Bottom, Left, Right }

internal sealed class DockSettings
{
    /// <summary>Resting icon size, in device-independent pixels (56 = the Dock of macOS Tahoe).</summary>
    public double IconSize { get; set; } = 56;
    public bool Magnification { get; set; } = true;
    public double MagnifiedSize { get; set; } = 76;
    public bool AutoHide { get; set; }
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>Hide the Windows taskbar while the dock runs (it always comes back when the dock ends).</summary>
    public bool ReplaceTaskbar { get; set; } = true;

    /// <summary>
    /// 安全模式：外壳只做"显示 Dock/菜单栏 + 启动应用"，不再介入全局输入、

    /// 不再抢占前台、不再动别的程序的窗口（键盘钩子/StripGuard/前台 hack 全部关闭）。
    /// 代价：F4 打开启动台失效、火狐那个 Battery Watcher 小窗会重新透出来。
    /// </summary>
    public bool SafeMode { get; set; }

    /// <summary>
    /// 歌词对齐偏移（毫秒）。网易云不对外报播放位置（SMTC 恒为 0），我们只能本地推算，
    /// 残留误差用这个值修：歌词偏慢就填负数（如 -1200），偏快就填正数。
    /// </summary>
    public int LyricOffsetMs { get; set; }

    /// <summary>Shows / hides the Windows taskbar again while the dock replaces it.</summary>
    public string TaskbarHotkey { get; set; } = "Ctrl+Alt+Shift+B";

    /// <summary>Start MacDock at sign-in (Run key). On by default: the dock replaces the taskbar.</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Win+1 … Win+9 start / activate the dock's apps (taken away from Explorer while the dock runs).</summary>
    public bool NumberShortcuts { get; set; } = true;

    /// <summary>打开启动台的按键（Windows 虚拟键码，例如 0xB3 = 键盘上的"媒体选台"；0 = 不绑定）。</summary>
    public int LaunchpadKey { get; set; }

    /// <summary>The macOS-style menu bar along the top of the screen (clock, tray icons, active app).</summary>
    public bool MenuBar { get; set; } = true;

    /// <summary>Interface language: "" follows Windows, otherwise "en" or "it".</summary>
    public string Language { get; set; } = "";

    /// <summary>旧版的"精灵"缩窗动画已删除：字段保留只为兼容老配置文件的解析。</summary>
    public bool Genie { get; set; }

    /// <summary>A menu bar on every monitor, as on macOS (the tray icons stay on the primary one).</summary>
    public bool MenuBarOnAllDisplays { get; set; } = true;

    /// <summary>The monitor the dock lives on (<c>\\.\DISPLAY2</c>); empty = the primary monitor, whichever it is.</summary>
    public string Display { get; set; } = "";

    /// <summary>
    /// As on macOS: pushing the pointer against the dock's edge of another monitor brings the dock there
    /// (until the next start, when it goes back to <see cref="Display"/>).
    /// </summary>
    public bool FollowPointer { get; set; } = true;

    public void Clamp()
    {
        IconSize = Math.Clamp(IconSize, 16, 128);
        MagnifiedSize = Math.Clamp(MagnifiedSize, IconSize, 256);
    }
}

internal sealed class DockItemConfig
{
    public string Name { get; set; } = "";

    /// <summary>A file, folder or shortcut path, or a shell parsing name such as <c>shell:AppsFolder\&lt;AUMID&gt;</c>.</summary>
    public string Target { get; set; } = "";
    public string? Arguments { get; set; }

    /// <summary>Launch the app when MacDock starts at sign-in ("Open at Login").</summary>
    public bool OpenAtStart { get; set; }

    /// <summary>点这个项时始终以管理员身份启动（UAC 会弹一次确认）。</summary>
    public bool RunAsAdmin { get; set; }
}

internal sealed class DockConfig
{
    const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public DockSettings Settings { get; set; } = new();
    public List<DockItemConfig> Items { get; set; } = [];

    /// <summary>Folders shown as stacks, between the running apps and the Recycle Bin.</summary>
    public List<DockItemConfig> Folders { get; set; } = [new DockItemConfig { Target = KnownFolders.Downloads }];

    /// <summary>当前生效的安全模式（由 Dock 启动时写入，WindowActions 等无实例的地方读它）。</summary>
    public static bool SafeMode { get; set; }

    /// <summary>歌词对齐偏移（毫秒），由 Dock 启动时从配置写入，供 IslandWindow 使用。</summary>
    public static int LyricOffsetMs { get; set; }

    /// <summary>是否用"读网易云内存里的播放时钟"来拿真实进度（SMTC 时间轴恒为 0 时的正解）。</summary>
    public static bool UseMemoryClock { get; set; } = true;

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacDock");

    public static string FilePath => Path.Combine(Folder, "config.json");

    /// <summary>
    /// The data folder of the first versions (called WinDock): its configuration is taken over once, so an
    /// existing dock keeps its apps and settings. Nothing is deleted there.
    /// </summary>
    static void MigrateLegacyFolder()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDock", "config.json");
            if (File.Exists(FilePath) || !File.Exists(legacy)) return;
            Directory.CreateDirectory(Folder);
            File.Copy(legacy, FilePath);
            Log.Info("Configuration taken over from the first version's data folder");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Migrating the configuration");
        }
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static DockConfig LoadOrCreate()
    {
        MigrateLegacyFolder();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Settings ??= new();
                    loaded.Items ??= [];
                    loaded.Folders ??= [];
                    loaded.Settings.Clamp();
                    if (loaded.Migrate()) loaded.Save();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Reading the configuration");
        }

        var config = new DockConfig { Items = DefaultApps.Discover() };
        config.Save();
        return config;
    }

    /// <summary>Brings a configuration written by an older version up to date; true if it changed.</summary>
    bool Migrate()
    {
        if (Version >= CurrentVersion) return false;
        // v2: the Start item, first in the dock.
        if (Version < 2 && !Items.Any(i => StartMenu.IsTarget(i.Target))) Items.Insert(0, StartMenu.CreateConfig());
        Version = CurrentVersion;
        return true;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Saving the configuration");
        }
    }
}
