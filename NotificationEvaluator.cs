using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// 一次额度阈值提醒。文本只使用受控的窗口名称和百分比，不包含账户或响应内容。
/// </summary>
internal sealed class UsageNotification
{
    public string Title { get; private set; }
    public string Message { get; private set; }

    public UsageNotification(string title, string message)
    {
        Title = title;
        Message = message;
    }
}

/// <summary>
/// 只在额度使用率从阈值以下跨越到阈值以上时提醒一次，避免每次刷新重复弹窗。
/// </summary>
internal sealed class NotificationEvaluator
{
    private const double ForecastWarningHours = 2d;
    private readonly IDictionary<string, NotificationState> thresholdStates = new Dictionary<string, NotificationState>(StringComparer.Ordinal);

    public IList<UsageNotification> Evaluate(QuotaSnapshot snapshot, int thresholdPercent)
    {
        return EvaluateWithOptions(snapshot, thresholdPercent, false);
    }

    /// <summary>
    /// 按用户选项计算阈值和周期重置提醒；默认 Evaluate 保持旧行为，避免改变已有调用方。
    /// </summary>
    public IList<UsageNotification> EvaluateWithOptions(QuotaSnapshot snapshot, int thresholdPercent, bool notifyOnReset)
    {
        return EvaluateWithInsights(snapshot, thresholdPercent, notifyOnReset, false, null);
    }

    /// <summary>
    /// 同时计算阈值、周期重置和本地耗尽预测提醒。预测提醒默认关闭，且按窗口和 reset_at 去重。
    /// </summary>
    public IList<UsageNotification> EvaluateWithInsights(
        QuotaSnapshot snapshot,
        int thresholdPercent,
        bool notifyOnReset,
        bool notifyOnForecast,
        IList<UsageInsight> insights)
    {
        List<UsageNotification> notifications = new List<UsageNotification>();
        if (snapshot == null || !snapshot.Success || snapshot.Windows == null)
        {
            return notifications;
        }

        int threshold = Math.Max(50, Math.Min(100, thresholdPercent));
        HashSet<string> observedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (QuotaWindow window in snapshot.Windows)
        {
            if (window == null)
            {
                continue;
            }

            string key = window.LimitWindowSeconds.ToString(CultureInfo.InvariantCulture);
            observedKeys.Add(key);
            bool crossed = window.UsedPercent >= threshold;
            NotificationState state;
            if (!thresholdStates.TryGetValue(key, out state))
            {
                // 首次查询只建立基线，避免程序启动时立即打扰用户。
                thresholdStates[key] = new NotificationState(crossed, window.ResetAt);
                continue;
            }

            if (IsResetCycle(state.ResetAt, window.ResetAt))
            {
                // reset_at 变化代表官方进入新周期；重置提醒和阈值提醒分别受控且各自只发一次。
                state.ResetAt = window.ResetAt;
                state.AboveThreshold = crossed;
                if (notifyOnReset)
                {
                    notifications.Add(CreateResetNotification(window));
                }
                if (crossed)
                {
                    notifications.Add(CreateNotification(window, threshold));
                }
                state.ForecastNotified = false;
                TryAddForecastNotification(notifications, state, window, FindInsight(insights, window.LimitWindowSeconds), notifyOnForecast);
                continue;
            }

            if (!state.ResetAt.HasValue && window.ResetAt.HasValue)
            {
                // 缺失字段恢复时只补齐时间基线，不把同一周期误判为新周期。
                state.ResetAt = window.ResetAt;
            }

            if (crossed && !state.AboveThreshold)
            {
                notifications.Add(CreateNotification(window, threshold));
            }
            state.AboveThreshold = crossed;
            TryAddForecastNotification(notifications, state, window, FindInsight(insights, window.LimitWindowSeconds), notifyOnForecast);
        }

        List<string> staleKeys = new List<string>();
        foreach (string key in thresholdStates.Keys)
        {
            if (!observedKeys.Contains(key))
            {
                staleKeys.Add(key);
            }
        }
        foreach (string staleKey in staleKeys)
        {
            thresholdStates.Remove(staleKey);
        }
        return notifications;
    }

    public void Reset()
    {
        thresholdStates.Clear();
    }

    private static string GetWindowLabel(int seconds)
    {
        if (seconds == 18000)
        {
            return "5 小时窗口";
        }
        if (seconds == 604800)
        {
            return "7 天窗口";
        }
        if (seconds >= 86400)
        {
            return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + " 天窗口";
        }
        if (seconds >= 3600)
        {
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " 小时窗口";
        }
        return "额度窗口";
    }

    private static UsageNotification CreateNotification(QuotaWindow window, int threshold)
    {
        return new UsageNotification(
            "额度提醒",
            GetWindowLabel(window.LimitWindowSeconds) + "使用率已达到 " + threshold.ToString(CultureInfo.InvariantCulture) + "%（当前 " +
            window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%）");
    }

    private static UsageNotification CreateResetNotification(QuotaWindow window)
    {
        string resetText = window.ResetAt.HasValue
            ? window.ResetAt.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : "时间未知";
        return new UsageNotification(
            "额度周期已重置",
            GetWindowLabel(window.LimitWindowSeconds) + "已进入新周期，下次重置 " + resetText);
    }

    private static UsageInsight FindInsight(IList<UsageInsight> insights, int limitWindowSeconds)
    {
        if (insights == null)
        {
            return null;
        }
        foreach (UsageInsight insight in insights)
        {
            if (insight != null && insight.LimitWindowSeconds == limitWindowSeconds)
            {
                return insight;
            }
        }
        return null;
    }

    private static void TryAddForecastNotification(
        IList<UsageNotification> notifications,
        NotificationState state,
        QuotaWindow window,
        UsageInsight insight,
        bool notifyOnForecast)
    {
        if (!notifyOnForecast || insight == null || !insight.ProjectedExhaustionAt.HasValue)
        {
            return;
        }

        double remainingHours = (insight.ProjectedExhaustionAt.Value - DateTimeOffset.Now).TotalHours;
        bool forecastSoon = remainingHours >= 0d && remainingHours <= ForecastWarningHours;
        if (!forecastSoon)
        {
            return;
        }
        if (!state.ForecastNotified)
        {
            notifications.Add(CreateForecastNotification(window, remainingHours));
            state.ForecastNotified = true;
        }
    }

    private static UsageNotification CreateForecastNotification(QuotaWindow window, double remainingHours)
    {
        string remainingText = remainingHours < 1d
            ? Math.Max(1, (int)Math.Round(remainingHours * 60d, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " 分钟"
            : remainingHours.ToString("0.#", CultureInfo.InvariantCulture) + " 小时";
        return new UsageNotification(
            "额度可能即将耗尽",
            GetWindowLabel(window.LimitWindowSeconds) + "按最近历史预计约 " + remainingText + " 后达到上限");
    }

    private static bool IsResetCycle(DateTimeOffset? previous, DateTimeOffset? current)
    {
        return previous.HasValue && current.HasValue && previous.Value != current.Value;
    }

    private sealed class NotificationState
    {
        public bool AboveThreshold { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
        public bool ForecastNotified { get; set; }

        public NotificationState(bool aboveThreshold, DateTimeOffset? resetAt)
        {
            AboveThreshold = aboveThreshold;
            ResetAt = resetAt;
            ForecastNotified = false;
        }
    }
}
