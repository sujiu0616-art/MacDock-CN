using System.Globalization;
using System.Text.RegularExpressions;

namespace MacDock.Core;

/// <summary>
/// 界面语言。源文本是英文：<see cref="T(string)"/> 按当前语言在简体中文或意大利语表里查，
/// 查不到就返回英文原文（所以缺一条译文时界面上永远不会是空白）。
/// </summary>
internal static class Loc
{
    /// <summary>当前语言，已归一化成 "en" / "it" / "zh" 之一（见 <see cref="Normalize"/>）。</summary>
    static string _language = "en";

    /// <summary>
    /// 设置里可选的语言代码（"" = 跟随 Windows）。顺序就是下拉项的显示顺序，
    /// 设置窗口用它生成列表，不要另写一份。
    /// </summary>
    public static readonly string[] Languages = ["", "zh-CN", "en", "it"];

    /// <param name="setting">""（跟随 Windows）、"zh-CN"、"en" 或 "it"。</param>
    public static void Init(string? setting)
    {
        // "" = 跟随 Windows：取当前界面区域性，所以中文系统上默认就是简体中文。
        string code = string.IsNullOrEmpty(setting) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        _language = Normalize(code);
    }

    /// <summary>
    /// 把任意语言代码归一化成"有译表的三选一"。只看前缀：zh / zh-CN / zh-Hans 都算简体中文
    /// （只有一份中文表，繁体区域也用它，总比掉回英文可用）；其余不认识的一律英文。
    /// </summary>
    static string Normalize(string code) =>
        code.Length >= 2 && code[..2].Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh"
        : code.Equals("it", StringComparison.OrdinalIgnoreCase) ? "it"
        : "en";

    /// <summary>
    /// 语言下拉项的文字：语言名一律用它自己的语言写（换错了语言也认得回来，同 macOS）。
    /// "" = 跟随 Windows，这一项要跟着当前界面语言翻译，所以走 <see cref="T(string)"/>。
    /// </summary>
    public static string LanguageName(string code) => code switch
    {
        "zh-CN" => "简体中文",
        "it" => "Italiano",
        "en" => "English",
        _ => T("Same as Windows"),
    };

    static Dictionary<string, string>? Table => _language switch
    {
        "zh" => Zh,
        "it" => It,
        _ => null,
    };

    public static string T(string english) => Table is { } table && table.TryGetValue(english, out var translated) ? translated : english;

    /// <summary>Same, then <see cref="string.Format(string, object[])"/> with the arguments.</summary>
    public static string T(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>
    /// 自检（<c>--selftest</c>）用：中文表的完整性问题清单，空表示没问题。
    /// 意大利语表的 key 就是源文本的完整清单，漏一条界面上就会露出英文；
    /// 另外每条译文必须原样保留 <c>{n}</c> 占位符，否则 <see cref="T(string, object[])"/> 会抛格式化异常。
    /// </summary>
    public static IReadOnlyList<string> ChineseTableProblems()
    {
        var problems = It.Keys.Where(english => !Zh.ContainsKey(english)).Select(english => $"missing: {english}").ToList();
        foreach (var (english, chinese) in Zh)
        {
            if (string.IsNullOrWhiteSpace(chinese))
            {
                problems.Add($"empty: {english}");
                continue;
            }
            if (!Placeholders(english).SequenceEqual(Placeholders(chinese))) problems.Add($"placeholders: {english} -> {chinese}");
        }
        return problems;
    }

    static IEnumerable<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order();

    static readonly Dictionary<string, string> It = new()
    {
        // Dock menus
        ["Open"] = "Apri",
        ["Empty Recycle Bin"] = "Svuota il Cestino",
        ["Recycle Bin"] = "Cestino",
        ["Show in File Explorer"] = "Mostra in Esplora file",
        ["File Explorer"] = "Esplora file",
        ["Remove from Dock"] = "Rimuovi dal Dock",
        ["Remove"] = "Rimuovi",
        ["Options"] = "Opzioni",
        ["Keep in Dock"] = "Mantieni nel Dock",
        ["Open at Login"] = "Apri all'avvio",
        ["Recent"] = "Recenti",
        ["New Window"] = "Nuova finestra",
        ["New Window as Administrator"] = "Nuova finestra come amministratore",
        ["Open as Administrator"] = "Apri come amministratore",
        ["Show All Windows"] = "Mostra tutte le finestre",
        ["Hide"] = "Nascondi",
        ["Quit"] = "Esci",
        ["{0} Settings…"] = "Impostazioni di {0}…",
        ["Replace the Windows Taskbar"] = "Sostituisci la barra delle applicazioni",
        ["Hide the Windows Taskbar"] = "Nascondi la barra di Windows",
        ["Show the Windows Taskbar"] = "Mostra la barra di Windows",
        ["Menu Bar"] = "Barra dei menu in alto",
        ["Start with Windows"] = "Avvia con Windows",
        ["Quit {0}"] = "Esci da {0}",
        // Menu bar
        ["Wi-Fi: signal {0}%"] = "Wi-Fi: segnale {0}%",
        ["Ethernet: connected"] = "Ethernet: connesso",
        ["No Internet connection"] = "Nessuna connessione a Internet",
        ["No audio device"] = "Nessun dispositivo audio",
        ["Volume: muted"] = "Volume: disattivato",
        ["Volume: {0}%"] = "Volume: {0}%",
        ["Battery: {0}%"] = "Batteria: {0}%",
        ["Battery: {0}% (charging)"] = "Batteria: {0}% (in carica)",
        ["Battery"] = "Batteria",
        // Stack
        ["This folder is empty"] = "La cartella è vuota",
        ["Open \"{0}\" in File Explorer"] = "Apri «{0}» in Esplora file",
        // Displays
        ["Display {0} ({1} × {2})"] = "Schermo {0} ({1} × {2})",
        ["Display {0} ({1} × {2}), primary"] = "Schermo {0} ({1} × {2}), principale",
        // Settings
        ["{0} Settings"] = "Impostazioni di {0}",
        ["Appearance"] = "Aspetto",
        ["Icon size"] = "Dimensione delle icone",
        ["Magnify icons under the pointer"] = "Ingrandimento al passaggio del mouse",
        ["Magnified size"] = "Dimensione ingrandita",
        ["Position"] = "Posizione",
        ["Position on screen"] = "Posizione sullo schermo",
        ["Bottom"] = "Basso",
        ["Left"] = "Sinistra",
        ["Right"] = "Destra",
        ["Hide automatically"] = "Nascondi automaticamente",
        ["Dock display"] = "Schermo del Dock",
        ["Primary"] = "Principale",
        ["{0} (disconnected)"] = "{0} (scollegato)",
        ["Follow the pointer to the bottom edge of another display"] = "Segui il puntatore sul bordo inferiore di un altro schermo",
        ["System"] = "Sistema",
        ["Replace the Windows taskbar"] = "Sostituisci la barra delle applicazioni di Windows",
        ["Menu bar on top"] = "Barra dei menu in alto",
        ["Menu bar on every display"] = "Barra dei menu su tutti gli schermi",
        ["Win+1 … Win+9 open the Dock's apps"] = "Win+1 … Win+9 per avviare le app del Dock",
        ["Show or hide the Windows taskbar: {0}"] = "Mostra o nascondi la barra di Windows: {0}",
        ["Language"] = "Lingua",
        ["Same as Windows"] = "Come Windows",
        ["English"] = "English",
        ["Italiano"] = "Italiano",
    };

    /// <summary>
    /// 简体中文。术语照 macOS 官方中文：Dock = 程序坞、菜单栏、文件资源管理器（Windows 的 Finder 位）、
    /// 回收站，Quit = 退出，Keep in Dock = 在程序坞中保留。key 与 <see cref="It"/> 一一对应，顺序也照它排，
    /// 新增译文时两边一起加（<c>--selftest</c> 会检查有没有漏）。
    /// </summary>
    static readonly Dictionary<string, string> Zh = new()
    {
        // Dock 菜单
        ["Open"] = "打开",
        ["Empty Recycle Bin"] = "清空回收站",
        ["Recycle Bin"] = "回收站",
        ["Show in File Explorer"] = "在文件资源管理器中显示",
        ["File Explorer"] = "文件资源管理器",
        ["Remove from Dock"] = "从程序坞中移除",
        ["Remove"] = "移除",
        ["Options"] = "选项",
        ["Keep in Dock"] = "在程序坞中保留",
        ["Open at Login"] = "登录时打开",
        ["Recent"] = "最近使用",
        ["New Window"] = "新建窗口",
        ["New Window as Administrator"] = "以管理员身份新建窗口",
        ["Open as Administrator"] = "以管理员身份打开",
        ["Show All Windows"] = "显示所有窗口",
        ["Hide"] = "隐藏",
        ["Quit"] = "退出",
        ["{0} Settings…"] = "{0} 设置…",
        ["Replace the Windows Taskbar"] = "替换 Windows 任务栏",
        ["Hide the Windows Taskbar"] = "隐藏 Windows 任务栏",
        ["Show the Windows Taskbar"] = "显示 Windows 任务栏",
        ["Menu Bar"] = "菜单栏",
        ["Start with Windows"] = "开机时启动",
        ["Quit {0}"] = "退出 {0}",
        // 菜单栏
        ["Wi-Fi: signal {0}%"] = "Wi-Fi：信号 {0}%",
        ["Ethernet: connected"] = "以太网：已连接",
        ["No Internet connection"] = "未连接互联网",
        ["No audio device"] = "无音频设备",
        ["Volume: muted"] = "音量：已静音",
        ["Volume: {0}%"] = "音量：{0}%",
        ["Battery: {0}%"] = "电池：{0}%",
        ["Battery: {0}% (charging)"] = "电池：{0}%（正在充电）",
        ["Battery"] = "电池",
        // 文件夹栈
        ["This folder is empty"] = "此文件夹为空",
        ["Open \"{0}\" in File Explorer"] = "在文件资源管理器中打开“{0}”",
        // 显示器
        ["Display {0} ({1} × {2})"] = "显示器 {0}（{1} × {2}）",
        ["Display {0} ({1} × {2}), primary"] = "显示器 {0}（{1} × {2}），主显示器",
        // 设置
        ["{0} Settings"] = "{0} 设置",
        ["Appearance"] = "外观",
        ["Icon size"] = "图标大小",
        ["Magnify icons under the pointer"] = "指针经过时放大图标",
        ["Magnified size"] = "放大后的尺寸",
        ["Position"] = "位置",
        ["Position on screen"] = "屏幕位置",
        ["Bottom"] = "底部",
        ["Left"] = "左侧",
        ["Right"] = "右侧",
        ["Hide automatically"] = "自动隐藏",
        ["Dock display"] = "程序坞所在显示器",
        ["Primary"] = "主显示器",
        ["{0} (disconnected)"] = "{0}（未连接）",
        ["Follow the pointer to the bottom edge of another display"] = "跟随指针移到另一台显示器的底边",
        ["System"] = "系统",
        ["Replace the Windows taskbar"] = "替换 Windows 任务栏",
        ["Menu bar on top"] = "顶部菜单栏",
        ["Menu bar on every display"] = "在每台显示器上显示菜单栏",
        ["Win+1 … Win+9 open the Dock's apps"] = "Win+1 … Win+9 打开程序坞中的应用",
        ["Show or hide the Windows taskbar: {0}"] = "显示或隐藏 Windows 任务栏：{0}",
        ["Language"] = "语言",
        ["Same as Windows"] = "跟随 Windows",
        ["English"] = "English",
        ["Italiano"] = "Italiano",
    };
}
