using System;
using System.IO;

/// <summary>
/// 统一应用本地数据目录。只返回本项目自己的目录，避免缓存/历史清理误触碰 Codex 凭据目录。
/// </summary>
internal static class LocalStoragePaths
{
    public static string RootDirectory
    {
        get
        {
            // 测试和受控诊断可显式指定隔离根目录；普通运行未设置时仍使用用户本地数据目录。
            string overrideRoot = Environment.GetEnvironmentVariable("STATUSBAR_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return Path.Combine(overrideRoot, "ChatGPTCodexUsageStatusBar");
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }
            return Path.Combine(root, "ChatGPTCodexUsageStatusBar");
        }
    }
}
