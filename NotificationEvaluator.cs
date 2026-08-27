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
    private readonly IDictionary<string, NotificationState> thresholdStates = new Dictionary<string, NotificationState>(StringComparer.Ordinal);

    public IList<UsageNotification> Evaluate(QuotaSnapshot snapshot, int thresholdPercent)
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
                // reset_at 变化代表官方进入新周期；新周期首次已超过阈值时立即提醒一次，否则等待正常跨越。
                state.ResetAt = window.ResetAt;
                state.AboveThreshold = crossed;
                if (crossed)
                {
                    notifications.Add(CreateNotification(window, threshold));
                }
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

    private static bool IsResetCycle(DateTimeOffset? previous, DateTimeOffset? current)
    {
        return previous.HasValue && current.HasValue && previous.Value != current.Value;
    }

    private sealed class NotificationState
    {
        public bool AboveThreshold { get; set; }
        public DateTimeOffset? ResetAt { get; set; }

        public NotificationState(bool aboveThreshold, DateTimeOffset? resetAt)
        {
            AboveThreshold = aboveThreshold;
            ResetAt = resetAt;
        }
    }
}
