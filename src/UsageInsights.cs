using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// 本地历史计算出的额度变化方向。它只描述趋势，不代表官方额度接口的承诺。
/// </summary>
internal enum UsageTrendDirection
{
    Unknown = 0,
    Rising = 1,
    Stable = 2,
    Falling = 3
}

/// <summary>
/// 单个额度窗口的本地洞察。所有字段都来自已经脱敏的窗口和历史摘要。
/// </summary>
internal sealed class UsageInsight
{
    public int LimitWindowSeconds { get; private set; }
    public double RatePerHour { get; private set; }
    public bool HasRate { get; private set; }
    public int SampleCount { get; private set; }
    public UsageTrendDirection Direction { get; private set; }
    public DateTimeOffset? ProjectedExhaustionAt { get; private set; }

    public UsageInsight(
        int limitWindowSeconds,
        double ratePerHour,
        int sampleCount,
        UsageTrendDirection direction,
        DateTimeOffset? projectedExhaustionAt)
    {
        LimitWindowSeconds = Math.Max(0, limitWindowSeconds);
        RatePerHour = IsFinite(ratePerHour) ? ratePerHour : 0d;
        SampleCount = Math.Max(0, sampleCount);
        Direction = direction;
        ProjectedExhaustionAt = projectedExhaustionAt;
        HasRate = SampleCount >= 2 && Math.Abs(RatePerHour) >= 0.05d;
    }

    public string GetRateText()
    {
        if (!HasRate)
        {
            return "等待历史趋势";
        }

        string sign = RatePerHour > 0d ? "+" : string.Empty;
        return "最近 " + sign + RatePerHour.ToString("0.#", CultureInfo.InvariantCulture) + "%/小时";
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}

/// <summary>
/// 计算本地额度趋势和健康标签。预测采用有限历史窗口的线性斜率，避免把瞬时值伪装成官方数据。
/// </summary>
internal static class UsageInsights
{
    private const double MinimumElapsedHours = 1d / 60d;
    private const double DirectionThreshold = 0.05d;
    private const double MaximumForecastHours = 24d * 30d;

    /// <summary>
    /// 为当前快照中的每个窗口生成一条洞察，顺序与官方窗口顺序保持一致。
    /// </summary>
    public static IList<UsageInsight> Build(
        UsageSnapshot snapshot,
        IList<HistoryPoint> history,
        DateTimeOffset now)
    {
        List<UsageInsight> insights = new List<UsageInsight>();
        if (snapshot == null || snapshot.Windows == null)
        {
            return insights;
        }

        foreach (UsageWindow window in snapshot.Windows)
        {
            if (window == null)
            {
                continue;
            }
            insights.Add(CreateInsight(
                snapshot.ProviderId,
                window,
                history,
                now,
                snapshot.Status == UsageStatus.Live && !snapshot.IsStale));
        }
        return insights;
    }

    /// <summary>
    /// 计算指定 Provider 和窗口的百分点/小时变化速度。数据不足或时间无效时返回零。
    /// </summary>
    public static double CalculateRate(
        IList<HistoryPoint> history,
        string providerId,
        int limitWindowSeconds,
        DateTimeOffset now)
    {
        List<HistoryPoint> points = CollectPoints(history, providerId, limitWindowSeconds, now);
        if (points.Count < 2)
        {
            return 0d;
        }

        DateTimeOffset origin = points[0].ObservedAt;
        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumXY = 0d;
        for (int index = 0; index < points.Count; index++)
        {
            HistoryPoint point = points[index];
            double x = (point.ObservedAt - origin).TotalHours;
            double y = point.UsedPercent;
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        double count = points.Count;
        double denominator = (count * sumXX) - (sumX * sumX);
        if (Math.Abs(denominator) < 0.000001d)
        {
            return 0d;
        }

        double rate = ((count * sumXY) - (sumX * sumY)) / denominator;
        return IsFinite(rate) ? Math.Max(-100d, Math.Min(100d, rate)) : 0d;
    }

    /// <summary>
    /// 返回用户能理解的整体健康标签；缓存、错误和高用量优先于趋势描述。
    /// </summary>
    public static string GetHealthLabel(
        UsageSnapshot snapshot,
        IList<HistoryPoint> history,
        DateTimeOffset now)
    {
        if (snapshot == null || snapshot.Status == UsageStatus.Loading)
        {
            return "读取中";
        }
        if (snapshot.IsStale)
        {
            return "数据已过期";
        }
        if (snapshot.Status != UsageStatus.Live)
        {
            return "需要操作";
        }
        if (snapshot.Windows == null || snapshot.Windows.Count == 0)
        {
            return "等待额度窗口";
        }

        bool hasHistory = false;
        foreach (UsageWindow window in snapshot.Windows)
        {
            if (window == null)
            {
                continue;
            }
            if (window.UsedPercent >= 95d)
            {
                return "额度紧张";
            }
            if (window.UsedPercent >= 80d)
            {
                return "接近阈值";
            }
            if (CollectPoints(history, snapshot.ProviderId, window.LimitWindowSeconds, now, window.ResetAt).Count >= 2)
            {
                hasHistory = true;
            }
        }
        return hasHistory ? "运行稳定" : "正在收集历史";
    }

    private static UsageInsight CreateInsight(
        string providerId,
        UsageWindow window,
        IList<HistoryPoint> history,
        DateTimeOffset now,
        bool allowForecast)
    {
        // 当前窗口有明确重置时间时，只把同一周期的样本纳入回归，避免旧周期拉偏斜率。
        // 缓存快照可以继续展示历史速率，但不再把过期数据推导成新的耗尽时间。
        List<HistoryPoint> points = CollectPoints(history, providerId, window.LimitWindowSeconds, now, window.ResetAt);
        double rate = CalculateRate(points, providerId, window.LimitWindowSeconds, now);
        UsageTrendDirection direction = UsageTrendDirection.Unknown;
        if (rate > DirectionThreshold)
        {
            direction = UsageTrendDirection.Rising;
        }
        else if (rate < -DirectionThreshold)
        {
            direction = UsageTrendDirection.Falling;
        }
        else if (points.Count >= 2)
        {
            direction = UsageTrendDirection.Stable;
        }

        DateTimeOffset? projected = null;
        if (allowForecast && rate > DirectionThreshold && window.UsedPercent < 100d && CanForecast(window, points, now))
        {
            double hours = (100d - window.UsedPercent) / rate;
            if (hours > 0d && hours <= MaximumForecastHours && IsFinite(hours))
            {
                try
                {
                    DateTimeOffset candidate = now.AddHours(hours);
                    // 官方周期会先于跨周期线性预测重置；跨过边界时宁可不显示估算。
                    if (candidate < window.ResetAt.Value)
                    {
                        projected = candidate;
                    }
                }
                catch (ArgumentOutOfRangeException)
                {
                    projected = null;
                }
            }
        }

        return new UsageInsight(window.LimitWindowSeconds, rate, points.Count, direction, projected);
    }

    private static bool CanForecast(UsageWindow window, IList<HistoryPoint> points, DateTimeOffset now)
    {
        if (window == null || !window.ResetAt.HasValue || window.ResetAt.Value <= now || points == null || points.Count < 2)
        {
            return false;
        }

        foreach (HistoryPoint point in points)
        {
            if (point == null || !point.ResetAt.HasValue || point.ResetAt.Value != window.ResetAt.Value)
            {
                return false;
            }
        }
        return true;
    }

    private static List<HistoryPoint> CollectPoints(
        IList<HistoryPoint> history,
        string providerId,
        int limitWindowSeconds,
        DateTimeOffset now,
        DateTimeOffset? resetAt = null)
    {
        List<HistoryPoint> points = new List<HistoryPoint>();
        if (history == null || limitWindowSeconds <= 0)
        {
            return points;
        }

        foreach (HistoryPoint point in history)
        {
            if (point == null || point.LimitWindowSeconds != limitWindowSeconds ||
                !string.Equals(point.ProviderId, providerId, StringComparison.Ordinal) ||
                point.ObservedAt > now || !IsFinite(point.UsedPercent))
            {
                continue;
            }
            if (resetAt.HasValue && (!point.ResetAt.HasValue || point.ResetAt.Value != resetAt.Value))
            {
                continue;
            }
            points.Add(point);
        }
        points.Sort(delegate(HistoryPoint left, HistoryPoint right)
        {
            return left.ObservedAt.CompareTo(right.ObservedAt);
        });
        if (points.Count >= 2)
        {
            double elapsedHours = (points[points.Count - 1].ObservedAt - points[0].ObservedAt).TotalHours;
            if (elapsedHours < MinimumElapsedHours)
            {
                points.Clear();
            }
        }
        return points;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
