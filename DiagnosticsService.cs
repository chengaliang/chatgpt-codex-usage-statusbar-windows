using System;
using System.Globalization;
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
        report.AppendLine(BuildOAuthLine(credentialDiagnostic));
        report.AppendLine(BuildProxyLine(proxyDiagnostic));
        report.AppendLine("查询状态：" + (safeSnapshot.Success ? "正常" : "未成功"));
        report.AppendLine("计划显示：" + DiagnosticSanitizer.PlanName(safeSnapshot.PlanName));
        report.AppendLine("额度窗口：" + (safeSnapshot.Windows == null ? 0 : safeSnapshot.Windows.Count).ToString(CultureInfo.InvariantCulture));
        report.AppendLine("最近查询：" + (safeSnapshot.QueriedAt.HasValue
            ? safeSnapshot.QueriedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未查询"));
        report.AppendLine("开机自启：" + (autoStartEnabled ? "已开启" : "已关闭"));
        report.AppendLine("刷新周期：" + safeSettings.RefreshIntervalMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟");
        report.AppendLine("背景样式：" + GetBackgroundStyleText(safeSettings.BackgroundStyle));
        report.AppendLine("通知：" + (safeSettings.NotificationsEnabled ? "已开启（阈值 " + safeSettings.NotificationThresholdPercent.ToString(CultureInfo.InvariantCulture) + "%）" : "已关闭"));
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
}
