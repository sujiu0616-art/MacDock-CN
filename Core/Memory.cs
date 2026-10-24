using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Core;

/// <summary>
/// 内存收尾。WPF 应用的工作集在启动、加载图标、打开启动台之后会留不少"已经不用但还挂着"的页，
/// 长时间空闲时把它们交还系统（EmptyWorkingSet）：任务管理器里看到的占用会明显下降。
///
/// 注意：这只影响**工作集**，私有字节不减 —— 交出去的页需要时再从页面文件读回来。
/// 所以只在真正空闲（没有动画、没有菜单、启动台没开）时做。
/// </summary>
internal static class Memory
{
    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>把工作集里暂时用不到的页交还系统。</summary>
    public static void Trim()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            EmptyWorkingSet(process.Handle);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Trimming the working set");
        }
    }

    /// <summary>一次紧凑 GC 再 trim：启动/关闭启动台这种"大批对象一次性释放"之后用。</summary>
    public static void Compact()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception) { }
        Trim();
    }
}
