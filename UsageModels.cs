using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Provider 最近一次查询的稳定状态。状态值用于 UI、缓存和诊断，不能由接口原文直接拼接。
/// </summary>
internal enum UsageStatus
{
    Loading = 0,
    Live = 1,
    Cached = 2,
    OAuthExpired = 3,
    NetworkError = 4,
    ApiError = 5,
    ParseError = 6,
    UnknownError = 7
}

/// <summary>
/// 与具体 Provider 无关的额度窗口模型。窗口名称从秒数生成，避免信任远端任意文本。
/// </summary>
internal sealed class UsageWindow
{
    public int LimitWindowSeconds { get; private set; }
    public double UsedPercent { get; private set; }
    public DateTimeOffset? ResetAt { get; private set; }
    public string DisplayName { get; private set; }

    public UsageWindow(int limitWindowSeconds, double usedPercent, DateTimeOffset? resetAt)
    {
        LimitWindowSeconds = Math.Max(0, limitWindowSeconds);
        UsedPercent = Math.Max(0d, Math.Min(100d, usedPercent));
        ResetAt = resetAt;
        DisplayName = CreateDisplayName(LimitWindowSeconds);
    }

    private static string CreateDisplayName(int seconds)
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
}

/// <summary>
/// Provider 输出给应用层的统一快照。它不包含账户标签、Token、原始响应或完整错误文本。
/// </summary>
internal sealed class UsageSnapshot
{
    public string ProviderId { get; private set; }
    public string PlanName { get; private set; }
    public IList<UsageWindow> Windows { get; private set; }
    public UsageStatus Status { get; private set; }
    public bool IsStale { get; private set; }
    public DateTimeOffset? LastLiveAt { get; private set; }
    public DateTimeOffset? QueriedAt { get; private set; }
    public string ErrorCode { get; private set; }

    private UsageSnapshot()
    {
        ProviderId = "chatgpt-codex";
        PlanName = "ChatGPT";
        Windows = new List<UsageWindow>();
        Status = UsageStatus.Loading;
        ErrorCode = string.Empty;
    }

    public static UsageSnapshot Loading(string providerId)
    {
        UsageSnapshot result = new UsageSnapshot();
        result.ProviderId = string.IsNullOrWhiteSpace(providerId) ? "chatgpt-codex" : providerId;
        return result;
    }

    public static UsageSnapshot LiveResult(string providerId, string planName, IList<UsageWindow> windows, DateTimeOffset queriedAt)
    {
        UsageSnapshot result = new UsageSnapshot();
        result.ProviderId = string.IsNullOrWhiteSpace(providerId) ? "chatgpt-codex" : providerId;
        result.PlanName = DiagnosticSanitizer.PlanName(planName);
        result.Windows = CopyWindows(windows);
        result.Status = UsageStatus.Live;
        result.IsStale = false;
        result.QueriedAt = queriedAt;
        result.LastLiveAt = queriedAt;
        return result;
    }

    public static UsageSnapshot Failure(string providerId, UsageStatus status, string errorCode, DateTimeOffset queriedAt)
    {
        UsageSnapshot result = Loading(providerId);
        result.Status = status;
        result.ErrorCode = errorCode ?? string.Empty;
        result.QueriedAt = queriedAt;
        return result;
    }

    public static UsageSnapshot CachedResult(
        string providerId,
        string planName,
        IList<UsageWindow> windows,
        DateTimeOffset? lastLiveAt)
    {
        UsageSnapshot result = Loading(providerId);
        result.PlanName = DiagnosticSanitizer.PlanName(planName);
        result.Windows = CopyWindows(windows);
        result.Status = UsageStatus.Cached;
        result.IsStale = true;
        result.LastLiveAt = lastLiveAt;
        result.QueriedAt = lastLiveAt;
        return result;
    }

    /// <summary>
    /// 将旧查询模型转换为统一快照，迁移期间仍允许旧 UI 使用 QuotaSnapshot 兼容视图。
    /// </summary>
    public static UsageSnapshot FromQuotaSnapshot(QuotaSnapshot source, string providerId)
    {
        if (source == null)
        {
            return Failure(providerId, UsageStatus.UnknownError, "empty_snapshot", DateTimeOffset.Now);
        }

        DateTimeOffset queriedAt = source.QueriedAt.HasValue ? source.QueriedAt.Value : DateTimeOffset.Now;
        if (source.Success)
        {
            List<UsageWindow> windows = new List<UsageWindow>();
            if (source.Windows != null)
            {
                foreach (QuotaWindow window in source.Windows)
                {
                    if (window != null)
                    {
                        windows.Add(new UsageWindow(window.LimitWindowSeconds, window.UsedPercent, window.ResetAt));
                    }
                }
            }
            return LiveResult(providerId, source.PlanName, windows, queriedAt);
        }

        return Failure(providerId, MapStatus(source.StatusText, source.ErrorText), MapErrorCode(source.StatusText, source.ErrorText), queriedAt);
    }

    public QuotaSnapshot ToQuotaSnapshot()
    {
        List<QuotaWindow> windows = new List<QuotaWindow>();
        foreach (UsageWindow window in Windows)
        {
            windows.Add(new QuotaWindow(window.DisplayName, window.LimitWindowSeconds, window.UsedPercent, window.ResetAt));
        }

        if (Status == UsageStatus.Live)
        {
            return QuotaSnapshot.SuccessResult(PlanName, "当前账户", windows);
        }
        if (Windows.Count > 0 && IsStale)
        {
            return QuotaSnapshot.CachedResult(
                PlanName,
                windows,
                LastLiveAt,
                GetStatusText(Status),
                GetRecoveryText(Status));
        }
        return QuotaSnapshot.Failure(GetStatusText(Status), GetRecoveryText(Status));
    }

    public UsageSnapshot WithCachedState(DateTimeOffset? lastLiveAt)
    {
        UsageSnapshot result = Clone();
        result.Status = UsageStatus.Cached;
        result.IsStale = true;
        result.LastLiveAt = lastLiveAt ?? result.LastLiveAt;
        return result;
    }

    public UsageSnapshot WithFailure(UsageStatus status, string errorCode, DateTimeOffset queriedAt)
    {
        UsageSnapshot result = Clone();
        result.Status = status;
        result.IsStale = result.Windows.Count > 0;
        result.ErrorCode = errorCode ?? string.Empty;
        result.QueriedAt = queriedAt;
        return result;
    }

    public UsageSnapshot Clone()
    {
        UsageSnapshot result = new UsageSnapshot();
        result.ProviderId = ProviderId;
        result.PlanName = PlanName;
        result.Windows = CopyWindows(Windows);
        result.Status = Status;
        result.IsStale = IsStale;
        result.LastLiveAt = LastLiveAt;
        result.QueriedAt = QueriedAt;
        result.ErrorCode = ErrorCode;
        return result;
    }

    private static IList<UsageWindow> CopyWindows(IList<UsageWindow> source)
    {
        List<UsageWindow> copy = new List<UsageWindow>();
        if (source == null)
        {
            return copy;
        }
        foreach (UsageWindow window in source)
        {
            if (window != null)
            {
                copy.Add(new UsageWindow(window.LimitWindowSeconds, window.UsedPercent, window.ResetAt));
            }
        }
        return copy;
    }

    private static UsageStatus MapStatus(string statusText, string errorText)
    {
        string value = (statusText ?? string.Empty) + " " + (errorText ?? string.Empty);
        if (value.IndexOf("OAuth", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return UsageStatus.OAuthExpired;
        }
        if (value.IndexOf("网络", StringComparison.Ordinal) >= 0 || value.IndexOf("超时", StringComparison.Ordinal) >= 0)
        {
            return UsageStatus.NetworkError;
        }
        if (value.IndexOf("接口", StringComparison.Ordinal) >= 0 || value.IndexOf("HTTP", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return UsageStatus.ApiError;
        }
        if (value.IndexOf("响应", StringComparison.Ordinal) >= 0 || value.IndexOf("JSON", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return UsageStatus.ParseError;
        }
        return UsageStatus.UnknownError;
    }

    private static string MapErrorCode(string statusText, string errorText)
    {
        switch (MapStatus(statusText, errorText))
        {
            case UsageStatus.OAuthExpired:
                return "oauth_unavailable";
            case UsageStatus.NetworkError:
                return "network_unavailable";
            case UsageStatus.ApiError:
                return "api_error";
            case UsageStatus.ParseError:
                return "parse_error";
            default:
                return "query_failed";
        }
    }

    private static string GetStatusText(UsageStatus status)
    {
        switch (status)
        {
            case UsageStatus.OAuthExpired:
                return "OAuth 不可用";
            case UsageStatus.NetworkError:
                return "网络不可用";
            case UsageStatus.ApiError:
                return "官方接口异常";
            case UsageStatus.ParseError:
                return "查询失败";
            default:
                return "查询失败";
        }
    }

    private static string GetRecoveryText(UsageStatus status)
    {
        switch (status)
        {
            case UsageStatus.OAuthExpired:
                return "请先在终端运行 codex login";
            case UsageStatus.NetworkError:
                return "请检查系统网络或 CLASH_MIXED_PROXY 配置";
            case UsageStatus.ApiError:
                return "额度接口暂时不可用，请稍后重试";
            case UsageStatus.ParseError:
                return "官方额度响应无法解析，请复制诊断信息";
            default:
                return "请点击刷新重试";
        }
    }
}
