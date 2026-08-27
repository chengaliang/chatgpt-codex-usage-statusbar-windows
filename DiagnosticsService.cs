using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// 单项诊断的固定状态。状态文本由本地白名单生成，不承载异常对象或远端原文。
/// </summary>
internal enum DiagnosticCheckStatus
{
    Pass = 0,
    Warning = 1,
    Fail = 2
}

/// <summary>
/// 可操作的安全诊断项。Detail 和 NextAction 只允许固定短文本，便于复制到 Issue。
/// </summary>
internal sealed class DiagnosticCheck
{
    public string Name { get; private set; }
    public DiagnosticCheckStatus Status { get; private set; }
    public string Detail { get; private set; }
    public string NextAction { get; private set; }

    public DiagnosticCheck(string name, DiagnosticCheckStatus status, string detail, string nextAction)
    {
        Name = Normalize(name, "检查");
        Status = Enum.IsDefined(typeof(DiagnosticCheckStatus), status) ? status : DiagnosticCheckStatus.Warning;
        Detail = Normalize(detail, "未提供状态");
        NextAction = Normalize(nextAction, "稍后重试");
    }

    private static string Normalize(string value, string fallback)
    {
        string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (normalized.Length == 0)
        {
            return fallback;
        }
        return normalized.Length > 100 ? normalized.Substring(0, 100) : normalized;
    }
}

/// <summary>
/// 诊断窗口一次展示的数据快照。窗口只持有已经脱敏的文本和固定诊断项。
/// </summary>
internal sealed class DiagnosticSnapshot
{
    public string Report { get; private set; }
    public IList<DiagnosticCheck> Checks { get; private set; }

    public DiagnosticSnapshot(string report, IList<DiagnosticCheck> checks)
    {
        Report = report ?? string.Empty;
        Checks = checks == null ? new List<DiagnosticCheck>() : new List<DiagnosticCheck>(checks);
    }
}

/// <summary>
/// 生成可提交到 Issue 的诊断摘要。输出采用固定字段白名单，避免把异常原文、路径、令牌或账户标识带出本机。
/// </summary>
internal sealed class DiagnosticsService
{
    public string Build(
        QuotaSnapshot snapshot,
        string credentialDiagnostic,
        string proxyDiagnostic,
        bool autoStartEnabled,
        bool hasAutoStartError,
        AppSettings settings)
    {
        QuotaSnapshot safeSnapshot = snapshot ?? QuotaSnapshot.Loading();
        AppSettings safeSettings = settings == null ? AppSettings.CreateDefault() : settings.Clone();
        safeSettings.Normalize();

        StringBuilder report = new StringBuilder();
        report.AppendLine("ChatGPT/Codex 状态栏诊断");
        report.AppendLine();
        report.AppendLine("系统：" + Environment.OSVersion.VersionString);
        report.AppendLine("运行时：.NET " + Environment.Version.ToString());
        report.AppendLine("进程：" + (IntPtr.Size * 8).ToString(CultureInfo.InvariantCulture) + " 位");
        report.AppendLine("应用版本：v" + UpdateService.CurrentVersion);
        report.AppendLine("DPI：每显示器感知");
        report.AppendLine(BuildOAuthLine(credentialDiagnostic));
        report.AppendLine(BuildProxyLine(proxyDiagnostic));
        string queryStatus = !safeSnapshot.Success ? "未成功" : (safeSnapshot.IsStale ? "缓存" : "正常");
        report.AppendLine("查询状态：" + queryStatus);
        report.AppendLine("计划显示：" + DiagnosticSanitizer.PlanName(safeSnapshot.PlanName));
        report.AppendLine("额度窗口：" + (safeSnapshot.Windows == null ? 0 : safeSnapshot.Windows.Count).ToString(CultureInfo.InvariantCulture));
        report.AppendLine("最近查询：" + (safeSnapshot.QueriedAt.HasValue
            ? safeSnapshot.QueriedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未查询"));
        if (safeSnapshot.LastLiveAt.HasValue)
        {
            report.AppendLine("最近成功：" + safeSnapshot.LastLiveAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
        report.AppendLine("开机自启：" + (autoStartEnabled ? "已开启" : "已关闭"));
        report.AppendLine("刷新周期：" + safeSettings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟");
        report.AppendLine("历史保留：" + safeSettings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture) + " 天");
        report.AppendLine("启动延迟：" + safeSettings.LaunchDelaySeconds.ToString(CultureInfo.InvariantCulture) + " 秒");
        report.AppendLine("启动更新检查：" + (safeSettings.AutoCheckUpdates ? "已开启（仅提示）" : "已关闭"));
        report.AppendLine("主题：" + GetThemeText(safeSettings.Theme));
        report.AppendLine("背景样式：" + GetBackgroundStyleText(safeSettings.BackgroundStyle));
        report.AppendLine("平滑动效：" + (safeSettings.AnimationsEnabled ? "已开启" : "已关闭"));
        report.AppendLine("通知：" + (safeSettings.NotificationsEnabled ? "已开启（阈值 " + safeSettings.NotificationThresholdPercent.ToString(CultureInfo.InvariantCulture) + "%）" : "已关闭"));
        report.AppendLine("本地缓存：" + (File.Exists(Path.Combine(LocalStoragePaths.RootDirectory, "cache.json")) ? "可用" : "暂无"));
        report.AppendLine("本地历史：" + (File.Exists(Path.Combine(LocalStoragePaths.RootDirectory, "history.json")) ? "可用" : "暂无"));
        if (hasAutoStartError)
        {
            report.AppendLine("启动项：检测到配置异常");
        }
        report.AppendLine();
        report.AppendLine("诊断信息不包含 Token、账户 ID、代理地址、文件路径或完整响应。");
        return report.ToString();
    }

    /// <summary>
    /// 生成固定白名单诊断项。每一项只给出状态和下一步，不把异常、路径或接口响应传入 UI。
    /// </summary>
    public IList<DiagnosticCheck> BuildChecks(
        QuotaSnapshot snapshot,
        string credentialDiagnostic,
        string proxyDiagnostic,
        bool autoStartEnabled,
        bool hasAutoStartError,
        AppSettings settings)
    {
        QuotaSnapshot safeSnapshot = snapshot ?? QuotaSnapshot.Loading();
        AppSettings safeSettings = settings == null ? AppSettings.CreateDefault() : settings.Clone();
        safeSettings.Normalize();
        List<DiagnosticCheck> checks = new List<DiagnosticCheck>();

        bool oauthReady = !string.IsNullOrWhiteSpace(credentialDiagnostic) &&
            credentialDiagnostic.IndexOf("可读取", StringComparison.Ordinal) >= 0;
        checks.Add(new DiagnosticCheck(
            "OAuth 配置",
            oauthReady ? DiagnosticCheckStatus.Pass : DiagnosticCheckStatus.Fail,
            oauthReady ? "ChatGPT OAuth 配置可读取" : "未读取到可用的 ChatGPT OAuth 配置",
            oauthReady ? "无需操作" : "在终端运行 codex login"));

        bool proxyInvalid = !string.IsNullOrWhiteSpace(proxyDiagnostic) &&
            proxyDiagnostic.IndexOf("无效", StringComparison.Ordinal) >= 0;
        checks.Add(new DiagnosticCheck(
            "网络代理",
            proxyInvalid ? DiagnosticCheckStatus.Warning : DiagnosticCheckStatus.Pass,
            proxyInvalid ? "自定义代理格式无效，程序已回退系统代理" : "网络模式可识别",
            proxyInvalid ? "检查 CLASH_MIXED_PROXY 的 http/https 格式" : "无需操作"));

        bool live = safeSnapshot.Success && !safeSnapshot.IsStale;
        bool cached = safeSnapshot.Success && safeSnapshot.IsStale;
        checks.Add(new DiagnosticCheck(
            "额度查询",
            live ? DiagnosticCheckStatus.Pass : (cached ? DiagnosticCheckStatus.Warning : DiagnosticCheckStatus.Fail),
            live ? "最近一次查询成功" : (cached ? "当前显示最近一次成功缓存" : "尚未取得可用额度数据"),
            live ? "无需操作" : (cached ? "检查网络后点击立即刷新" : "检查 OAuth 与网络后点击立即刷新")));

        int windowCount = safeSnapshot.Windows == null ? 0 : safeSnapshot.Windows.Count;
        checks.Add(new DiagnosticCheck(
            "额度窗口",
            windowCount > 0 ? DiagnosticCheckStatus.Pass : DiagnosticCheckStatus.Warning,
            windowCount > 0 ? windowCount.ToString(CultureInfo.InvariantCulture) + " 个官方窗口可展示" : "官方响应暂未提供额度窗口",
            windowCount > 0 ? "无需操作" : "稍后重试，接口字段可能发生变化"));

        bool cacheAvailable = File.Exists(Path.Combine(LocalStoragePaths.RootDirectory, "cache.json"));
        checks.Add(new DiagnosticCheck(
            "本地缓存",
            cacheAvailable ? DiagnosticCheckStatus.Pass : DiagnosticCheckStatus.Warning,
            cacheAvailable ? "最近成功摘要已保存" : "暂无本地成功缓存",
            cacheAvailable ? "无需操作" : "成功刷新后会自动创建"));

        bool historyAvailable = File.Exists(Path.Combine(LocalStoragePaths.RootDirectory, "history.json"));
        checks.Add(new DiagnosticCheck(
            "本地历史",
            historyAvailable ? DiagnosticCheckStatus.Pass : DiagnosticCheckStatus.Warning,
            historyAvailable ? "趋势历史文件可用" : "暂无本地趋势历史",
            historyAvailable ? "保留周期：" + safeSettings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture) + " 天" : "成功刷新两次后开始记录"));

        checks.Add(new DiagnosticCheck(
            "开机自启",
            hasAutoStartError ? DiagnosticCheckStatus.Fail : DiagnosticCheckStatus.Pass,
            hasAutoStartError ? "启动项检测到配置异常" : (autoStartEnabled ? "当前用户开机自启已开启" : "当前用户开机自启已关闭"),
            hasAutoStartError ? "在设置中重新保存开机启动" : "无需操作"));

        return checks;
    }

    private static string BuildOAuthLine(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.IndexOf("可读取", StringComparison.Ordinal) >= 0
            ? "OAuth：ChatGPT OAuth 配置可读取"
            : "OAuth：不可用";
    }

    private static string BuildProxyLine(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.IndexOf("自定义", StringComparison.Ordinal) >= 0
            ? "网络：自定义代理（地址已隐藏）"
            : "网络：Windows 系统代理或直连";
    }

    private static string GetBackgroundStyleText(BackgroundStyle style)
    {
        switch (style)
        {
            case BackgroundStyle.SemiTransparent:
                return "半透明";
            case BackgroundStyle.HighTransparency:
                return "高透明";
            default:
                return "实色";
        }
    }

    private static string GetThemeText(ThemeMode mode)
    {
        switch (mode)
        {
            case ThemeMode.System:
                return "跟随系统";
            case ThemeMode.Light:
                return "浅色";
            case ThemeMode.Graphite:
                return "石墨";
            case ThemeMode.Dark:
                return "深色";
            default:
                return "跟随系统";
        }
    }
}
