using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace MacDock.Core;

/// <summary>
/// The commands the installer, the uninstaller and the CI use. None of them opens a window, so they are safe to run
/// while the dock is (or is not) running.
/// </summary>
internal static class CommandLine
{
    /// <summary>Message the running dock answers by quitting normally (taskbar restored, app bars released).</summary>
    public const string QuitMessageName = "MacDock.Quit";
    /// <summary>Broadcast after the config changed on disk so a running dock can pick it up.</summary>
    public const string ReloadMessageName = "MacDock.ReloadConfig";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    /// <summary>Handles <paramref name="args"/> if it is one of these commands; null when the dock should start instead.</summary>
    public static int? Run(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--quit":
                return Quit() ? 0 : 1;
            case "--uninstall":
                // Leaves the PC as it was: dock closed, taskbar back, no start-up entries.
                Quit();
                TaskbarState.Restore(force: true);
                AutoStart.Set(false);
                AutoStart.SetRestoreFallback(false);
                return 0;
            case "--add-to-dock":
                return AddToDock(args.Skip(1));
            case "--set-autostart":
                return SetAutostart(args.ElementAtOrDefault(1));
            case "--selftest":
                return SelfTest();
            case "--version":
                Print(Version);
                return 0;
            default:
                return null;
        }
    }

    public static string Version =>
        typeof(CommandLine).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Asks a running dock to quit and waits for it (and its guard) to be gone.</summary>
    /// <summary>
    /// Appends the given files/shortcuts to the pinned list and tells the running dock to reload.
    /// Used by the Explorer context-menu entry ("添加到拓展坞").
    /// </summary>
    static int AddToDock(IEnumerable<string> paths)
    {
        var targets = paths
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0 && (File.Exists(p) || Directory.Exists(p)))
            .ToList();
        if (targets.Count == 0) return 2;

        var config = DockConfig.LoadOrCreate();
        int added = 0;
        foreach (var target in targets)
        {
            if (config.Items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase))) continue;
            config.Items.Add(new DockItemConfig { Name = DisplayName(target), Target = target });
            added++;
        }
        if (added == 0) return 0;   // 已经在里面：不算失败，只是没事可做

        config.Save();
        Broadcast(ReloadMessageName);
        Print($"added {added} item(s) to the dock");
        return 0;
    }

    /// <summary>File name without extension; a folder keeps its own name.</summary>
    static string DisplayName(string target)
    {
        var name = Path.GetFileNameWithoutExtension(target);
        return string.IsNullOrWhiteSpace(name) ? Path.GetFileName(target) : name;
    }

    /// <summary>Post a registered message to the dock window so it reloads the config from disk.</summary>
    static void Broadcast(string messageName)
    {
        uint msg = RegisterWindowMessage(messageName);
        if (msg == 0) return;
        var hwnd = FindWindow(null, AppInfo.Name);
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
    }

    static bool Quit()
    {
        var running = Process.GetProcessesByName(AppInfo.Name).Where(p => p.Id != Environment.ProcessId).ToList();
        if (running.Count == 0) return true;
        var dock = FindWindow(null, AppInfo.Name);
        if (dock != IntPtr.Zero) PostMessage(dock, RegisterWindowMessage(QuitMessageName), IntPtr.Zero, IntPtr.Zero);
        bool gone = running.All(p => p.WaitForExit(8000));
        if (!gone)
        {
            // Did not answer: stop it. The guard sees that and restores the taskbar.
            foreach (var p in running.Where(p => !p.HasExited)) p.Kill();
        }
        foreach (var guard in Process.GetProcessesByName(AppInfo.Name + "Guard")) guard.WaitForExit(5000);
        return true;
    }

    static int SetAutostart(string? value)
    {
        bool on = value is "on" or "1" or "true";
        var config = DockConfig.LoadOrCreate();
        config.Settings.StartWithWindows = on;
        config.Save();
        AutoStart.Set(on);
        return 0;
    }

    /// <summary>
    /// A quick check for the CI and for users who report a problem: the parts that need no window, each on its own line.
    /// Exit code 0 when everything passed.
    /// </summary>
    static int SelfTest()
    {
        int failures = 0;
        void Check(string name, Func<string> test)
        {
            try
            {
                Print($"ok    {name}: {test()}");
            }
            catch (Exception ex)
            {
                failures++;
                Print($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Print($"{AppInfo.Name} {Version} self-test");
        Check("displays", () =>
        {
            var all = Displays.All();
            if (all.Count == 0) throw new InvalidOperationException("no display");
            return string.Join(", ", all.Select(d => $"{d.DeviceName} {d.Bounds.Width}x{d.Bounds.Height} @{d.Scale:0.##}"));
        });
        Check("language", () =>
        {
            Loc.Init("en");
            string en = Loc.T("Quit {0}", AppInfo.Name);
            Loc.Init("it");
            string it = Loc.T("Quit {0}", AppInfo.Name);
            Loc.Init("zh-CN");
            string zh = Loc.T("Quit {0}", AppInfo.Name);
            if (it == en) throw new InvalidOperationException("Italian table not used");
            if (zh == en) throw new InvalidOperationException("Chinese table not used");
            if (zh == it) throw new InvalidOperationException("the Chinese table is the Italian one");
            var problems = Loc.ChineseTableProblems(); // 中文表漏 key 或丢了 {n} 占位符
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("; ", problems));
            // 下拉项由 Loc.Languages 生成：显示名重复说明有语言没配名字，选中项会错位。
            var names = Loc.Languages.Select(Loc.LanguageName).ToList();
            if (names.Distinct().Count() != names.Count) throw new InvalidOperationException("duplicate language names: " + string.Join(", ", names));
            // "" = 跟随 Windows：中文 Windows 上默认就该是中文，不然默认语言选错了。
            Loc.Init("");
            if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" && Loc.T("Quit {0}", AppInfo.Name) != zh)
                throw new InvalidOperationException("\"\" does not follow the Chinese Windows UI language");
            return $"{en} / {it} / {zh}";
        });
        Check("layout", () =>
        {
            var m = new DockMetrics(48, 80, DockEdge.Bottom);
            var layout = new DockLayout();
            var slots = Enumerable.Repeat(new DockSlot(true, 1), 10).ToList();
            layout.Compute(m, slots, 1920, 960, 1);
            if (layout.PlateStart < 0 || layout.PlateEnd > 1920) throw new InvalidOperationException("plate off screen");
            return $"plate {layout.PlateStart:0}..{layout.PlateEnd:0}";
        });
        Check("shell icons", () =>
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            return ShellItems.Icon(explorer) is { } icon ? $"{icon.Width}x{icon.Height}" : throw new InvalidOperationException("no icon");
        });
        Check("taskbar", () => TaskbarState.FindExplorerTray() != IntPtr.Zero ? "Explorer taskbar found" : "no Explorer taskbar (service session)");
        Print(failures == 0 ? "passed" : $"{failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A WinExe has no console: write to the one of the command prompt that started it, if any.</summary>
    static void Print(string line)
    {
        if (!_attached)
        {
            AttachConsole(-1);
            _attached = true;
        }
        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // No console (started from Explorer): nothing to show.
        }
    }

    static bool _attached;
}
