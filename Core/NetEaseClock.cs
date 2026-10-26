using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacDock.Core;

/// <summary>
/// 网易云播放时钟探测：不依赖版本偏移，靠"数值随时间线性推进"从进程内存里找出真实播放位置。
///
/// 背景：网易云的原生 SMTC 时间轴恒为 0（start=end=pos=00:00:00），做不了歌词同步；
/// 但它自己的播放器内部维护着毫秒/秒级进度。这里用只读方式（PROCESS_VM_READ）把它读出来，
/// 思路与 xw66/Cloud-Music-overlay-for-Forza-Horizon 的 NeteaseMemoryClockScanner 一致：
///   1) 扫私有可写内存，收集"落在合理范围内"的 int64(毫秒) 与 double(秒) 候选
///   2) 隔 400ms 复读，只留"涨了约 400ms"的候选
///   3) 再复读一次确认线性，缓存地址后单点读（每 250ms 一次，开销可忽略）
///
/// 只读、不写对方内存、不注入代码。
/// </summary>
internal sealed class NetEaseClock : IDisposable
{
    // ── Win32 ───────────────────────────────────────────────────────────
    const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int PROCESS_VM_READ = 0x0010;
    const uint MEM_COMMIT = 0x1000;
    const uint MEM_PRIVATE = 0x20000;
    const uint PAGE_GUARD = 0x100;
    const uint PAGE_NOACCESS = 0x01;
    const uint PAGE_READWRITE = 0x04;
    const uint PAGE_WRITECOPY = 0x08;
    const uint PAGE_EXECUTE_READWRITE = 0x40;
    const uint PAGE_EXECUTE_WRITECOPY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint __alignment2;
    }

    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32", SetLastError = true)]
    static extern UIntPtr VirtualQueryEx(IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION64 info, UIntPtr length);

    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr read);

    // ── 状态 ────────────────────────────────────────────────────────────
    public const int MinMs = 3_000;
    public const int MaxMs = 2_000_000;      // 33 分钟
    const int ChunkBytes = 4 * 1024 * 1024;
    const int MaxCandidates = 200_000;

    readonly object _gate = new();
    IntPtr _handle;
    int _pid;
    long _address;
    byte _kind;                              // 0 = int64 毫秒, 1 = double 秒
    bool _ready;
    bool _scanning;
    long _lastScanStartMs;
    double _verifiedSeconds = -1;
    long _verifiedAtMs;

    public bool IsReady { get { lock (_gate) return _ready; } }
    public long Address { get { lock (_gate) return _address; } }
    public int ProcessId { get { lock (_gate) return _pid; } }

    /// <summary>换歌 / 用户拖进度 / 值突然跳变时调用：丢弃缓存地址，下次重新扫描。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _ready = false;
            _address = 0;
            _verifiedSeconds = -1;
        }
    }

    /// <summary>读当前播放秒数；未就绪或读取失败返回 false。</summary>
    public bool TryGetSeconds(out double seconds)
    {
        seconds = 0;
        long address;
        byte kind;
        IntPtr handle;
        lock (_gate)
        {
            if (!_ready) return false;
            address = _address;
            kind = _kind;
            handle = _handle;
        }
        if (handle == IntPtr.Zero || address == 0) return false;

        var raw = ReadBytes(handle, address, 8);
        if (raw is null) { Invalidate(); return false; }

        double value = kind == 0
            ? BitConverter.ToInt64(raw, 0) / 1000.0
            : BitConverter.ToDouble(raw, 0);
        if (double.IsNaN(value) || value < 0) { Invalidate(); return false; }

        lock (_gate)
        {
            // 值回退（换歌/重播）或跳变过大 → 交给上层做一致化判断，这里只回报数值
            _verifiedSeconds = value;
            _verifiedAtMs = Environment.TickCount64;
        }
        seconds = value;
        return true;
    }

    /// <summary>驱动扫描（每秒调用一次即可，内部自带节流与去重）。</summary>
    /// <param name="expectedSeconds">
    /// 我们独立估算的位置（只在"亲眼看到换歌、之后一直在跟"时可信）。
    /// 传进来后扫描会优先选与之吻合的候选，用来排除"应用运行时长"这类同样 1:1 增长的计数器。
    /// </param>
    /// <param name="durationSeconds">已知时长，超过它的候选直接丢弃。</param>
    public void EnsureScan(double? expectedSeconds = null, double? durationSeconds = null, int cooldownMs = 8_000)
    {
        lock (_gate)
        {
            if (_ready || _scanning) return;
            if (Environment.TickCount64 - _lastScanStartMs < cooldownMs) return;
            _scanning = true;
            _lastScanStartMs = Environment.TickCount64;
        }
        _ = Task.Run(() =>
        {
            try { ScanOnce(expectedSeconds, durationSeconds); }
            catch (Exception ex) { Log.Error(ex, "NetEase clock scan"); }
            finally { lock (_gate) _scanning = false; }
        });
    }

    // ── 扫描 ────────────────────────────────────────────────────────────
    void ScanOnce(double? expectedSeconds, double? durationSeconds)
    {
        int pid = FindMainProcess();
        if (pid <= 0) return;

        IntPtr handle;
        lock (_gate)
        {
            if (_handle != IntPtr.Zero && _pid != pid) { CloseHandleQuietly(_handle); _handle = IntPtr.Zero; }
            if (_handle == IntPtr.Zero)
            {
                _handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
                _pid = pid;
            }
            handle = _handle;
        }
        if (handle == IntPtr.Zero) return;

        var first = Collect(handle, out long scanned);
        if (first.Count == 0) { Log.Info($"Media: clock scan pid={pid} scanned={scanned / 1024 / 1024}MB no candidates"); return; }

        Thread.Sleep(420);
        var second = new List<(long address, byte kind, double first, double second)>();
        foreach (var item in first)
        {
            if (!TryReadValue(handle, item.address, item.kind, out double value)) continue;
            double deltaMs = (value - item.value) * 1000.0;
            if (deltaMs >= 250 && deltaMs <= 700) second.Add((item.address, item.kind, item.value, value));
        }
        if (second.Count == 0) { Log.Info($"Media: clock scan pid={pid} candidates={first.Count} no advancing"); return; }

        Thread.Sleep(420);
        (long address, byte kind, double value, double score)? best = null;
        int rejectedByDuration = 0;
        foreach (var item in second)
        {
            if (!TryReadValue(handle, item.address, item.kind, out double value)) continue;
            double deltaMs = (value - item.second) * 1000.0;
            if (deltaMs < 250 || deltaMs > 700) continue;

            // 时长上限：网易云自己的"运行时长"这类计数器会在这一步被筛掉
            if (durationSeconds is { } duration && value > duration + 3) { rejectedByDuration++; continue; }
            if (value < 0) continue;

            // 打分：有独立估算值时按吻合度；否则优先"秒"制（播放器内部多用 double 秒）
            double score = expectedSeconds is { } expected
                ? Math.Abs(value - expected)
                : (item.kind == 1 ? 0 : 1);
            if (best is null || score < best.Value.score)
                best = (item.address, item.kind, value, score);
        }

        if (best is { } chosen)
        {
            // 预期值只用于排序，不做硬性拒绝：外推本身可能偏，不能因此否掉真值
            lock (_gate)
            {
                _address = chosen.address;
                _kind = chosen.kind;
                _ready = true;
            }
            Log.Info($"Media: clock found pid={pid} addr=0x{chosen.address:X} kind={(chosen.kind == 0 ? "ms" : "sec")} value={chosen.value:F1}s " +
                     $"expected={(expectedSeconds.HasValue ? expectedSeconds.Value.ToString("F1") : "n/a")} rejectedByDuration={rejectedByDuration} scanned={scanned / 1024 / 1024}MB");
            return;
        }
        Log.Info($"Media: clock scan pid={pid} second={second.Count} rejectedByDuration={rejectedByDuration} - no usable clock");
    }

    static int FindMainProcess()
    {
        int best = 0;
        long bestSize = 0;
        foreach (var process in Process.GetProcessesByName("cloudmusic"))
        {
            try
            {
                // 主进程加载 cloudmusic.dll；辅助进程没有
                // 真正在放音的进程会加载 MF 媒体引擎；只加载 cloudmusic.dll 的多是渲染/辅助进程
                bool hasCore = false;
                bool hasEngine = false;
                foreach (ProcessModule module in process.Modules)
                {
                    if (string.Equals(module.ModuleName, "cloudmusic.dll", StringComparison.OrdinalIgnoreCase)) hasCore = true;
                    else if (string.Equals(module.ModuleName, "MFMediaEngine.dll", StringComparison.OrdinalIgnoreCase)) hasEngine = true;
                }
                if (!hasCore) continue;
                long score = (hasEngine ? 1L << 40 : 0) + process.WorkingSet64;
                if (score > bestSize) { bestSize = score; best = process.Id; }
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return best;
    }

    List<(long address, byte kind, double value)> Collect(IntPtr handle, out long scannedBytes)
    {
        var found = new List<(long, byte, double)>();
        scannedBytes = 0;
        ulong address = 0;
        var info = default(MEMORY_BASIC_INFORMATION64);
        nuint size = (nuint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION64>();

        while (found.Count < MaxCandidates)
        {
            if (VirtualQueryEx(handle, (IntPtr)(long)address, out info, (UIntPtr)size) == UIntPtr.Zero) break;
            ulong baseAddress = info.BaseAddress;
            ulong regionSize = info.RegionSize;

            bool usable = info.State == MEM_COMMIT
                          && info.Type == MEM_PRIVATE
                          && (info.Protect & PAGE_GUARD) == 0
                          && (info.Protect & PAGE_NOACCESS) == 0
                          && IsReadable(info.Protect)
                          && regionSize > 8
                          && regionSize <= 512UL * 1024 * 1024;

            if (usable)
            {
                ulong offset = 0;
                while (offset < regionSize && found.Count < MaxCandidates)
                {
                    int chunk = (int)Math.Min((ulong)ChunkBytes, regionSize - offset);
                    if (chunk < 8) break;
                    var buffer = ReadBytes(handle, (long)(baseAddress + offset), chunk);
                    offset += (ulong)chunk;
                    if (buffer is null) continue;
                    scannedBytes += buffer.Length;
                    CollectChunk(buffer, (long)(baseAddress + offset - (ulong)chunk), found);
                }
            }

            if (regionSize == 0) break;
            address = baseAddress + regionSize;
            if (address <= baseAddress) break;
        }
        return found;
    }

    static bool IsReadable(uint protect)
    {
        uint value = protect & 0xFF;
        return value == PAGE_READWRITE || value == PAGE_WRITECOPY
            || value == PAGE_EXECUTE_READWRITE || value == PAGE_EXECUTE_WRITECOPY;
    }

    static void CollectChunk(byte[] buffer, long baseAddress, List<(long, byte, double)> found)
    {
        int usable = buffer.Length - (buffer.Length % 8);
        for (int i = 0; i < usable; i += 8)
        {
            long asInt = BitConverter.ToInt64(buffer, i);
            if (asInt >= MinMs && asInt <= MaxMs)
            {
                found.Add((baseAddress + i, 0, asInt / 1000.0));
                continue;
            }
            double asDouble = BitConverter.ToDouble(buffer, i);
            if (asDouble >= MinMs / 1000.0 && asDouble <= MaxMs / 1000.0 && !double.IsNaN(asDouble))
                found.Add((baseAddress + i, 1, asDouble));
            if (found.Count >= MaxCandidates) return;
        }
    }

    static bool TryReadValue(IntPtr handle, long address, byte kind, out double value)
    {
        value = 0;
        var raw = ReadBytes(handle, address, 8);
        if (raw is null) return false;
        value = kind == 0 ? BitConverter.ToInt64(raw, 0) / 1000.0 : BitConverter.ToDouble(raw, 0);
        return !double.IsNaN(value) && value >= 0;
    }

    static byte[]? ReadBytes(IntPtr handle, long address, int size)
    {
        var buffer = new byte[size];
        if (!ReadProcessMemory(handle, (IntPtr)address, buffer, (UIntPtr)(uint)size, out UIntPtr read)) return null;
        if (read == UIntPtr.Zero) return null;
        int got = (int)read;
        if (got == size) return buffer;
        var trimmed = new byte[got];
        Array.Copy(buffer, trimmed, got);
        return trimmed;
    }

    static void CloseHandleQuietly(IntPtr handle)
    {
        try { if (handle != IntPtr.Zero) CloseHandle(handle); } catch (Exception) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseHandleQuietly(_handle);
            _handle = IntPtr.Zero;
            _ready = false;
        }
    }
}
