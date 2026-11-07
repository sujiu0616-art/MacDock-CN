namespace MacDock.Core;

/// <summary>
/// Dock geometry in "dock space": <c>u</c> runs along the dock, <c>v</c> is the distance from the
/// screen edge. Everything is in device-independent pixels and derived from the icon size, so the
/// proportions stay the same at every size, like the macOS Dock.
/// </summary>
internal sealed class DockMetrics(double iconSize, double maxSize, DockEdge edge)
{
    public double IconSize { get; } = iconSize;
    public double MaxSize { get; } = maxSize;
    public DockEdge Edge { get; } = edge;

    /// <summary>Gap between the screen edge and the dock plate.</summary>
    public double EdgeGap => Math.Round(IconSize * 0.09);
    /// <summary>Plate padding around the icons.</summary>
    public double Pad => Math.Round(IconSize * 0.06);   // 板高 = 图标 + 2*Pad ≈ 1.12×（更扁；macOS 约 1.12~1.15×）
    /// <summary>Space between two resting icons.</summary>
    public double Gap => IconSize * 0.135;   // 图标之间的间距（0.075 -> 0.105 -> 0.135）
    /// <summary>Width of the separator slot between pinned and running apps (the line sits in its middle).</summary>
    public double SeparatorWidth => Math.Round(IconSize * 0.28);
    /// <summary>How far from the pointer icons are still magnified.</summary>
    public double Range => IconSize * 2.6;
    public double PlateThickness => IconSize + 2 * Pad;
    /// <summary>
    /// Corner radius of the plate: a share of its height (the Dock grows rounder with bigger icons),
    /// within the range macOS stays in so the plate never looks like a pill or a rectangle.
    /// </summary>
    public double PlateRadius => Math.Clamp(PlateThickness * 0.26, 14, 30);
    public double DotSize => Math.Max(3, Math.Round(IconSize * 0.085));
    /// <summary>Highest an icon can reach: magnified, or resting and bouncing.</summary>
    public double TallestIcon => Math.Max(MaxSize, IconSize * 1.7);
    /// <summary>Room for the name label beyond the tallest icon.</summary>
    public double LabelRoom => Edge is DockEdge.Bottom or DockEdge.Top ? 44 + IconSize * 1.6 : 260; // also room to drag an icon out of the dock
    public double WindowThickness => EdgeGap + Pad + TallestIcon + LabelRoom;
}

/// <summary>One entry of the strip: an icon (magnifies) or a separator (fixed width).</summary>
internal readonly record struct DockSlot(bool IsIcon, double Presence);

internal sealed class DockLayout
{
    double[] _restStarts = [], _restSlots = [], _magSlots = [];

    /// <summary>Start of each entry along the dock.</summary>
    public double[] Starts { get; private set; } = [];
    /// <summary>Drawn size of each entry (icons are square; separators use it as their width).</summary>
    public double[] Sizes { get; private set; } = [];
    public double PlateStart { get; private set; }
    public double PlateEnd { get; private set; }
    public double RestPlateStart { get; private set; }
    public double RestPlateEnd { get; private set; }

    /// <param name="length">Length of the dock window along the edge.</param>
    /// <param name="pointer">Pointer position along the dock, or null when it has never been over it.</param>
    /// <param name="amount">0 = at rest, 1 = fully magnified (animated on enter / leave).</param>
    public void Compute(DockMetrics m, IReadOnlyList<DockSlot> slots, double length, double? pointer, double amount, double uniform = 1, double rangeScale = 1,
        double? pointer2 = null, double amount2 = 0, double rangeScale2 = 1)
    {
        int n = slots.Count;
        if (Starts.Length != n)
        {
            Starts = new double[n];
            Sizes = new double[n];
            _restStarts = new double[n];
            _restSlots = new double[n];
            _magSlots = new double[n];
        }

        // Each entry owns its trailing gap and shrinks with it while it appears / disappears,
        // so the total is simply the sum minus the one gap after the last entry.
        double s = m.IconSize, g = m.Gap;
        double restTotal = 0;
        for (int i = 0; i < n; i++)
        {
            double width = slots[i].IsIcon ? s : m.SeparatorWidth;
            _restSlots[i] = (width + g) * slots[i].Presence;
            restTotal += _restSlots[i];
        }
        double restLength = Math.Max(s, restTotal - g);
        double restStart = (length - restLength) / 2;
        RestPlateStart = restStart - m.Pad;
        RestPlateEnd = restStart + restLength + m.Pad;

        // ── 放大布局：照搬 smoothui 的 Dock 实现（有源码可对照）──
        // https://github.com/educlopez/smoothui/blob/main/packages/smoothui/components/dock/index.tsx
        //
        // centreAt(i) = 静止中心 + gain * (∫falloff(i 到指针) - centreShift)
        //   · 每个图标只看"自己到指针的距离"，**不累加**左边图标的增量
        //     （累加会让左边任何一个放大都把右边整排推走）
        //   · centreShift 用**首尾两个图标**的解析积分求居中补偿，不取样固定几个，
        //     所以指针移动时它是连续函数，没有索引跳变带来的阶跃
        double gain = amount * (m.MaxSize / s - 1);
        double distance = m.Range * rangeScale;
        double gain2 = amount2 * (m.MaxSize / s - 1);
        double distance2 = m.Range * rangeScale2;

        double pos = restStart;
        for (int i = 0; i < n; i++)
        {
            _restStarts[i] = pos;
            Sizes[i] = (slots[i].IsIcon ? s : m.SeparatorWidth) * slots[i].Presence;
            Starts[i] = pos;
            pos += _restSlots[i];
        }

        double shift = 0, shift2 = 0;
        bool magnifying = gain > 0.0001 && pointer is double && n > 1;
        if (magnifying)
        {
            double ptr = pointer!.Value - restStart;
            double first = (_restStarts[0] - restStart + _restSlots[0] / 2) - ptr;
            double last = (_restStarts[n - 1] - restStart + _restSlots[n - 1] / 2) - ptr;
            shift = (FalloffIntegral(first, distance) + FalloffIntegral(last, distance)) * 0.5
                  + s * (Falloff(last, distance) - Falloff(first, distance)) * 0.25;
        }

        // 呼吸也用同一条通路（放大 + 推动），只是中心是通知图标。
        // 之前"只放大不推动"会让图标互相盖住，丑；压幅度又不够明显。
        bool breathing = gain2 > 0.0001 && pointer2 is double && n > 1;
        if (breathing)
        {
            double p2r = pointer2!.Value - restStart;
            double f2 = (_restStarts[0] - restStart + _restSlots[0] / 2) - p2r;
            double l2 = (_restStarts[n - 1] - restStart + _restSlots[n - 1] / 2) - p2r;
            shift2 = (FalloffIntegral(f2, distance2) + FalloffIntegral(l2, distance2)) * 0.5
                   + s * (Falloff(l2, distance2) - Falloff(f2, distance2)) * 0.25;
        }

        for (int i = 0; i < n; i++)
        {
            if (!slots[i].IsIcon) continue;
            double centreRest = _restStarts[i] - restStart + _restSlots[i] / 2;

            double scale = 1;
            double centre = centreRest;
            if (magnifying)
            {
                double delta = centreRest - (pointer!.Value - restStart);
                scale += gain * Falloff(delta, distance);
                centre += gain * (FalloffIntegral(delta, distance) - shift);
            }

            if (breathing)
            {
                double delta2 = centreRest - (pointer2!.Value - restStart);
                scale += gain2 * Falloff(delta2, distance2);
                centre += gain2 * (FalloffIntegral(delta2, distance2) - shift2);
            }
            // 两个 bump 叠起来最多到"悬停最大尺寸"，不再往上涨
            scale = Math.Min(scale, m.MaxSize / s);
            Sizes[i] = s * slots[i].Presence * scale;
            Starts[i] = restStart + centre - Sizes[i] / 2;
        }

        // 槽位（ItemAt / 相邻间隙用）：一格 = 从本图标左边到下一个图标左边
        for (int i = 0; i < n; i++)
        {
            _magSlots[i] = i < n - 1
                ? Math.Max(Sizes[i], Starts[i + 1] - Starts[i])
                : Sizes[i];
        }

        // 不越出屏幕：整排一起平移（只在贴边时触发，中间滑动时恒不生效）
        if (n > 0)
        {
            double left = Starts[0] - m.Pad;
            double right = Starts[n - 1] + Sizes[n - 1] + m.Pad;
            if (left < 0) { for (int i = 0; i < n; i++) Starts[i] -= left; right -= left; }
            if (right > length) { double d = right - length; for (int i = 0; i < n; i++) Starts[i] -= d; }
        }
        PlateStart = n > 0 ? Starts[0] - m.Pad : restStart - m.Pad;
        PlateEnd = n > 0 ? Starts[n - 1] + Sizes[n - 1] + m.Pad : restStart + restLength + m.Pad;
    }

    /// <summary>抬余弦衰减：指针处为 1，到影响半径处平缓归零。</summary>
    static double Falloff(double delta, double distance)
    {
        if (distance <= 0) return 0;
        double spread = Math.Abs(delta);
        if (spread >= distance) return 0;
        return (Math.Cos(Math.PI * spread / distance) + 1) * 0.5;
    }

    /// <summary>衰减函数的解析积分（smoothui 的 falloffIntegral）：位移用它算，才和放大量严格对应。</summary>
    static double FalloffIntegral(double delta, double distance)
    {
        if (distance <= 0) return 0;
        double sign = delta < 0 ? -1 : 1;
        double spread = Math.Min(Math.Abs(delta), distance);
        return sign * (distance / (2 * Math.PI) * Math.Sin(Math.PI * spread / distance) + spread * 0.5);
    }


    /// <summary>Where a resting position ends up in the magnified strip (relative to its start).</summary>
    double MapRestToMagnified(double p, double restStart, double restLength, double magLength)
    {
        if (p <= restStart) return p - restStart;
        if (p >= restStart + restLength) return magLength + (p - restStart - restLength);

        double mag = 0;
        for (int i = 0; i < _restSlots.Length; i++)
        {
            double local = p - _restStarts[i];
            if (local < _restSlots[i] || i == _restSlots.Length - 1)
                return mag + (_restSlots[i] > 0 ? local / _restSlots[i] * _magSlots[i] : 0);
            mag += _magSlots[i];
        }
        return mag;
    }

    /// <summary>Index of the entry at <paramref name="u"/> (gaps belong to the nearer entry), or -1.</summary>
    public int ItemAt(double u)
    {
        for (int i = 0; i < Starts.Length; i++)
        {
            if (_magSlots[i] <= 0.01) continue;
            double gapAfter = _magSlots[i] - Sizes[i];
            double gapBefore = i == 0 ? 0 : _magSlots[i - 1] - Sizes[i - 1];
            if (u >= Starts[i] - gapBefore / 2 && u <= Starts[i] + Sizes[i] + gapAfter / 2) return i;
        }
        return -1;
    }

    /// <summary>一个高斯放大 bump 在某个槽位上的增量（0 表示不在影响半径内）。</summary>
    static double Bump(double? centre, double amount, double rangeScale, double slotCentre, DockMetrics m, double iconSize)
    {
        if (centre is not double c || amount <= 0) return 0;
        double range = m.Range * rangeScale;
        double d = Math.Abs(c - slotCentre);
        if (d >= range) return 0;
        return (m.MaxSize / iconSize - 1) * (1 + Math.Cos(Math.PI * d / range)) / 2 * amount;
    }
}
