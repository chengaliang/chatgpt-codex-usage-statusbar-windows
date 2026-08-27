using System;
using Microsoft.Win32;

/// <summary>
/// 管理当前 Windows 用户的开机启动项。实际命令写入 Run 键，配置标记放到独立键。
/// </summary>
internal sealed class StartupManager
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsRegistryPath = @"Software\ChatGPTCodexUsageStatusBar";
    private const string StartupValueName = "ChatGPTCodexUsageStatusBar";
    private const string StartupConfiguredValueName = "ChatGPTCodexUsageStatusBarConfigured";
    private readonly string executablePath;

    public StartupManager(string executablePath)
    {
        this.executablePath = executablePath ?? string.Empty;
    }

    /// <summary>
    /// 读取并必要时初始化当前用户的启动项。首次运行默认开启，并迁移旧版本错误标记。
    /// </summary>
    public bool TryGetEnabled(out bool enabled, out string error)
    {
        enabled = false;
        error = string.Empty;
        try
        {
            using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            using (RegistryKey settingsKey = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
            {
                if (runKey == null || settingsKey == null)
                {
                    error = "无法访问当前用户启动项";
                    return false;
                }

                // 旧版本曾把 marker 写进 Run，先迁移再删除，避免 Windows 把“1”当作启动命令。
                object legacyConfigured = runKey.GetValue(StartupConfiguredValueName);
                object configured = settingsKey.GetValue(StartupConfiguredValueName);
                if (configured == null && legacyConfigured != null)
                {
                    settingsKey.SetValue(StartupConfiguredValueName, legacyConfigured, RegistryValueKind.String);
                    configured = legacyConfigured;
                }
                if (legacyConfigured != null)
                {
                    runKey.DeleteValue(StartupConfiguredValueName, false);
                }

                if (configured == null)
                {
                    runKey.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                    settingsKey.SetValue(StartupConfiguredValueName, "1", RegistryValueKind.String);
                    enabled = true;
                    return true;
                }

                string command = runKey.GetValue(StartupValueName) as string;
                enabled = !string.IsNullOrWhiteSpace(command);
                if (enabled && !string.Equals(command, GetStartupCommand(), StringComparison.OrdinalIgnoreCase))
                {
                    // 程序被移动后修复旧路径，避免开机启动指向不存在的文件。
                    runKey.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                }
                return true;
            }
        }
        catch (Exception)
        {
            enabled = false;
            error = "无法写入当前用户启动项";
            return false;
        }
    }

    /// <summary>
    /// 写入或删除当前用户的启动命令，并保留“已配置”标记以记住用户选择。
    /// </summary>
    public bool TrySetEnabled(bool enabled, out string error)
    {
        error = string.Empty;
        try
        {
            using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            using (RegistryKey settingsKey = Registry.CurrentUser.CreateSubKey(SettingsRegistryPath))
            {
                if (runKey == null || settingsKey == null)
                {
                    error = "无法访问当前用户启动项";
                    return false;
                }

                if (enabled)
                {
                    runKey.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                }
                else
                {
                    runKey.DeleteValue(StartupValueName, false);
                }

                // 即使用户从旧版本升级，也要清理错误的 Run marker。
                runKey.DeleteValue(StartupConfiguredValueName, false);
                settingsKey.SetValue(StartupConfiguredValueName, "1", RegistryValueKind.String);
                return true;
            }
        }
        catch (Exception)
        {
            error = "无法更新当前用户启动项";
            return false;
        }
    }

    private string GetStartupCommand()
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("应用程序路径不可用");
        }

        // 双引号保证安装路径含空格时仍能正确启动。
        return "\"" + executablePath + "\"";
    }
}
