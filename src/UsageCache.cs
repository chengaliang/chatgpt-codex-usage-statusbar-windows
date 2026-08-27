using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

/// <summary>
/// 保存最近一次成功额度摘要，并在下一次启动时以 Cached 状态恢复。缓存 DTO 不允许携带账户或凭据字段。
/// </summary>
internal sealed class UsageCache
{
    private readonly JavaScriptSerializer serializer;

    public string CachePath { get; private set; }

    public UsageCache()
        : this(Path.Combine(LocalStoragePaths.RootDirectory, "cache.json"))
    {
    }

    internal UsageCache(string cachePath)
    {
        CachePath = cachePath;
        serializer = new JavaScriptSerializer();
    }

    public UsageSnapshot Load()
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(CachePath, Encoding.UTF8);
            CacheDocument document = serializer.Deserialize<CacheDocument>(json);
            if (document == null || string.IsNullOrWhiteSpace(document.ProviderId) || document.Windows == null || document.Windows.Count == 0)
            {
                BackupBrokenFile();
                return null;
            }

            List<UsageWindow> windows = new List<UsageWindow>();
            foreach (CacheWindowDocument item in document.Windows)
            {
                if (item == null || item.LimitWindowSeconds <= 0)
                {
                    continue;
                }
                DateTimeOffset? resetAt = ParseDate(item.ResetAt);
                windows.Add(new UsageWindow(item.LimitWindowSeconds, item.UsedPercent, resetAt));
            }
            if (windows.Count == 0)
            {
                BackupBrokenFile();
                return null;
            }

            return UsageSnapshot.CachedResult(
                document.ProviderId,
                document.PlanName,
                windows,
                ParseDate(document.LastLiveAt));
        }
        catch (Exception)
        {
            BackupBrokenFile();
            return null;
        }
    }

    public void Save(UsageSnapshot snapshot)
    {
        string error;
        if (!TrySave(snapshot, out error))
        {
            throw new IOException(error);
        }
    }

    public bool TrySave(UsageSnapshot snapshot, out string error)
    {
        error = string.Empty;
        string temporaryPath = CachePath + ".tmp";
        try
        {
            if (snapshot == null || snapshot.Status != UsageStatus.Live || snapshot.Windows == null || snapshot.Windows.Count == 0)
            {
                return true;
            }

            CacheDocument document = new CacheDocument();
            document.Version = 1;
            document.ProviderId = snapshot.ProviderId;
            document.PlanName = DiagnosticSanitizer.PlanName(snapshot.PlanName);
            document.LastLiveAt = FormatDate(snapshot.LastLiveAt ?? snapshot.QueriedAt);
            document.Windows = new List<CacheWindowDocument>();
            foreach (UsageWindow window in snapshot.Windows)
            {
                if (window == null)
                {
                    continue;
                }
                document.Windows.Add(new CacheWindowDocument
                {
                    LimitWindowSeconds = window.LimitWindowSeconds,
                    UsedPercent = window.UsedPercent,
                    ResetAt = FormatDate(window.ResetAt)
                });
            }
            if (document.Windows.Count == 0)
            {
                return true;
            }

            string directory = Path.GetDirectoryName(CachePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "缓存路径无效";
                return false;
            }
            Directory.CreateDirectory(directory);
            string json = serializer.Serialize(document);
            using (StreamWriter writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }
            ReplaceAtomically(temporaryPath);
            return true;
        }
        catch (Exception)
        {
            error = "无法保存额度缓存";
            TryDelete(temporaryPath);
            return false;
        }
    }

    /// <summary>
    /// 清理本项目生成的缓存及其临时备份，不会访问或删除 Codex CLI 的凭据目录。
    /// </summary>
    public void Clear()
    {
        TryDelete(CachePath);
        TryDelete(CachePath + ".tmp");
        TryDelete(CachePath + ".bak");
    }

    private void ReplaceAtomically(string temporaryPath)
    {
        if (!File.Exists(CachePath))
        {
            File.Move(temporaryPath, CachePath);
            return;
        }
        try
        {
            File.Replace(temporaryPath, CachePath, null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(temporaryPath, CachePath, true);
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            File.Copy(temporaryPath, CachePath, true);
            File.Delete(temporaryPath);
        }
    }

    private void BackupBrokenFile()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }
            string backupPath = CachePath + ".bak";
            TryDelete(backupPath);
            File.Move(CachePath, backupPath);
        }
        catch (Exception)
        {
            // 缓存备份失败不应阻止主程序使用网络查询。
        }
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        DateTimeOffset parsed;
        if (string.IsNullOrWhiteSpace(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
        {
            return null;
        }
        return parsed;
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
            // 临时文件清理失败不覆盖原始错误。
        }
    }

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public string ProviderId { get; set; }
        public string PlanName { get; set; }
        public string LastLiveAt { get; set; }
        public List<CacheWindowDocument> Windows { get; set; }
    }

    private sealed class CacheWindowDocument
    {
        public int LimitWindowSeconds { get; set; }
        public double UsedPercent { get; set; }
        public string ResetAt { get; set; }
    }
}
