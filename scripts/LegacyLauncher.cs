using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

/// <summary>
/// 兼容旧版本写入的根目录启动项，将旧路径转发到新的 dist 主程序。
/// </summary>
internal static class LegacyLauncher
{
    [STAThread]
    private static void Main()
    {
        try
        {
            string distributionDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dist");
            string executablePath = Path.Combine(distributionDirectory, "SubscriptionStatus.exe");
            if (!File.Exists(executablePath))
            {
                MessageBox.Show(
                    "SubscriptionStatus.exe was not found in the dist folder.",
                    "ChatGPT Codex Usage Status Bar",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = distributionDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(
                "The status bar could not be started from the dist folder.",
                "ChatGPT Codex Usage Status Bar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
