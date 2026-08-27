using System;
using System.Globalization;
using System.IO;
using System.Text;

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
