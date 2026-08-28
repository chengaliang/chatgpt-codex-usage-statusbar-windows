using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

/// <summary>
/// 保存当前用户的非敏感设置。写入采用临时文件和替换，损坏时自动备份并回退默认值。
/// </summary>
internal sealed class SettingsStore
{
    private readonly JavaScriptSerializer serializer;

    public string SettingsPath { get; private set; }

    public SettingsStore()
        : this(GetDefaultSettingsPath())
    {
    }

    internal SettingsStore(string settingsPath)
    {
        SettingsPath = settingsPath;
        serializer = new JavaScriptSerializer();
    }

    public AppSettings Load()
    {
        AppSettings defaults = AppSettings.CreateDefault();
        if (!File.Exists(SettingsPath))
        {
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                BackupBrokenFile();
                return defaults;
            }

            AppSettings settings = serializer.Deserialize<AppSettings>(json);
            if (settings == null)
            {
                BackupBrokenFile();
                return defaults;
            }

            // 旧版本没有 OpacityPercent 字段；先根据原有背景档位迁移，再执行统一规范化，避免升级后突然恢复为实色。
            IDictionary<string, object> rawValues = serializer.DeserializeObject(json) as IDictionary<string, object>;
            if (rawValues == null || !rawValues.ContainsKey("OpacityPercent"))
            {
                settings.OpacityPercent = AppSettings.GetOpacityForStyle(settings.BackgroundStyle);
            }

            settings.Normalize();
            return settings;
        }
        catch (Exception)
        {
            // 设置损坏不应阻止状态栏启动；备份原文件便于用户排查但不展示其内容。
            BackupBrokenFile();
            return defaults;
        }
    }

    public void Save(AppSettings settings)
    {
        string error;
        if (!TrySave(settings, out error))
        {
            throw new IOException(error);
        }
    }

    /// <summary>
    /// 尝试保存设置并返回稳定错误文本，供 UI 在无权限或磁盘异常时提示用户。
    /// </summary>
    public bool TrySave(AppSettings settings, out string error)
    {
        error = string.Empty;
        string temporaryPath = SettingsPath + ".tmp";
        try
        {
            AppSettings normalized = settings == null ? AppSettings.CreateDefault() : settings.Clone();
            normalized.Normalize();
            string directory = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "本地设置路径无效";
                return false;
            }

            Directory.CreateDirectory(directory);
            string json = serializer.Serialize(normalized);
            using (StreamWriter writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }

            ReplaceAtomically(temporaryPath);
            return true;
        }
        catch (Exception)
        {
            error = "无法保存本地设置";
            TryDelete(temporaryPath);
            return false;
        }
    }

    private void ReplaceAtomically(string temporaryPath)
    {
        if (!File.Exists(SettingsPath))
        {
            File.Move(temporaryPath, SettingsPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, SettingsPath, null);
        }
        catch (PlatformNotSupportedException)
        {
            // 某些文件系统不支持 Replace，退回覆盖并确保临时文件不残留。
            File.Copy(temporaryPath, SettingsPath, true);
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // 目标文件可能位于不支持原子替换的路径，使用同样的可恢复覆盖策略。
            File.Copy(temporaryPath, SettingsPath, true);
            File.Delete(temporaryPath);
        }
    }

    private void BackupBrokenFile()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            string backupPath = SettingsPath + ".bak";
            TryDelete(backupPath);
            File.Move(SettingsPath, backupPath);
        }
        catch (Exception)
        {
            // 备份失败不影响默认设置回退，也不把文件内容或异常堆栈暴露给用户。
        }
    }

    private static string GetDefaultSettingsPath()
    {
        return Path.Combine(LocalStoragePaths.RootDirectory, "settings.json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 清理失败不应覆盖原始错误，也不能打断状态栏启动。
        }
    }
}
