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
