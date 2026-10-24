using System.Collections.Concurrent;
using System.Windows.Media;

namespace MacDock.Core;

/// <summary>
/// 图标缓存：同一个目标只解一次 shell 图标。
///
/// 每次新窗口出现，Dock 都会为它新建一个运行项；原来每条都要走一遍 Shell 的图标解出（COM 调用，
/// 几十到几百毫秒，且在 UI 线程上）—— 启动应用后那一下卡顿就是这么来的。
/// </summary>
internal static class Icons
{
    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        if (Cache.TryGetValue(target, out var cached)) return cached;
        ImageSource? icon = IconArt.Squircle(ShellItems.Icon(target));
        Cache[target] = icon;
        return icon;
    }
}
