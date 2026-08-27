using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

/// <summary>
/// 本地额度趋势点。只保存 Provider、窗口秒数、百分比、重置时间和观测时间。
/// </summary>
internal sealed class HistoryPoint
{
    public string ProviderId { get; private set; }
    public int LimitWindowSeconds { get; private set; }
    public double UsedPercent { get; private set; }
    public DateTimeOffset? ResetAt { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }

    public HistoryPoint(string providerId, int limitWindowSeconds, double usedPercent, DateTimeOffset? resetAt, DateTimeOffset observedAt)
    {
        ProviderId = string.IsNullOrWhiteSpace(providerId) ? "chatgpt-codex" : providerId;
        LimitWindowSeconds = Math.Max(0, limitWindowSeconds);
        UsedPercent = Math.Max(0d, Math.Min(100d, usedPercent));
        ResetAt = resetAt;
        ObservedAt = observedAt;
    }
}

/// <summary>
/// 以原子 JSON 文件维护有限历史，避免为了趋势图引入数据库或运行时依赖。
/// </summary>
internal sealed class HistoryStore
{
    private const int MaxPoints = 500;
    private readonly JavaScriptSerializer serializer;
    private int retentionDays;

    public string HistoryPath { get; private set; }
    public int RetentionDays
    {
        get { return retentionDays; }
        private set { retentionDays = NormalizeRetentionDays(value); }
    }

    public HistoryStore()
        : this(Path.Combine(LocalStoragePaths.RootDirectory, "history.json"), 30)
    {
    }

    public HistoryStore(int retentionDays)
        : this(Path.Combine(LocalStoragePaths.RootDirectory, "history.json"), retentionDays)
    {
    }

    internal HistoryStore(string historyPath)
        : this(historyPath, 30)
    {
    }

    internal HistoryStore(string historyPath, int retentionDays)
    {
        HistoryPath = historyPath;
        RetentionDays = retentionDays;
        serializer = new JavaScriptSerializer();
        if (File.Exists(HistoryPath))
        {
            // 启动阶段先清理已有超期记录，避免离线期间一直不刷新而绕过新的保留周期。
            Load();
        }
    }

    /// <summary>
    /// 更新历史保留周期；仅在已有历史文件时立即裁剪，避免用户改设置时凭空创建空文件。
    /// </summary>
    public void SetRetentionDays(int days)
    {
        RetentionDays = days;
        if (File.Exists(HistoryPath))
        {
            Trim();
        }
    }

    public IList<HistoryPoint> Load()
    {
        List<HistoryPoint> points = new List<HistoryPoint>();
        if (!File.Exists(HistoryPath))
        {
            return points;
        }
        try
        {
            string json = File.ReadAllText(HistoryPath, Encoding.UTF8);
            HistoryDocument document = serializer.Deserialize<HistoryDocument>(json);
            if (document == null || document.Points == null)
            {
                BackupBrokenFile();
                return points;
            }
            bool skippedInvalidPoint = false;
            foreach (HistoryPointDocument item in document.Points)
            {
                DateTimeOffset observedAt;
                if (item == null || string.IsNullOrWhiteSpace(item.ProviderId) || item.LimitWindowSeconds <= 0 ||
                    !TryParseDate(item.ObservedAt, out observedAt))
                {
                    skippedInvalidPoint = true;
                    continue;
                }
                points.Add(new HistoryPoint(
                    item.ProviderId,
                    item.LimitWindowSeconds,
                    item.UsedPercent,
                    ParseDate(item.ResetAt),
                    observedAt));
            }
            int countBeforeTrim = points.Count;
            Trim(points, DateTimeOffset.UtcNow);
            if (skippedInvalidPoint || points.Count != countBeforeTrim)
            {
                // 读取时同步回写裁剪结果，避免旧记录继续留在磁盘上绕过新的隐私保留设置。
                Save(points);
            }
            return points;
        }
        catch (Exception)
        {
            BackupBrokenFile();
            return points;
        }
    }

    public void Append(UsageSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Status != UsageStatus.Live || snapshot.Windows == null || !snapshot.QueriedAt.HasValue)
        {
            return;
        }

        List<HistoryPoint> points = new List<HistoryPoint>(Load());
        DateTimeOffset observedAt = snapshot.QueriedAt.Value.ToUniversalTime();
        foreach (UsageWindow window in snapshot.Windows)
        {
            if (window == null || window.LimitWindowSeconds <= 0)
            {
                continue;
            }
            RemoveDuplicate(points, snapshot.ProviderId, window.LimitWindowSeconds, observedAt);
            points.Add(new HistoryPoint(snapshot.ProviderId, window.LimitWindowSeconds, window.UsedPercent, window.ResetAt, observedAt));
        }
        Trim(points, DateTimeOffset.UtcNow);
        Save(points);
    }

    public void Trim()
    {
        List<HistoryPoint> points = new List<HistoryPoint>(Load());
        Trim(points, DateTimeOffset.UtcNow);
        Save(points);
    }

    public void Clear()
    {
        TryDelete(HistoryPath);
        TryDelete(HistoryPath + ".tmp");
        TryDelete(HistoryPath + ".bak");
    }

    private void Save(IList<HistoryPoint> points)
    {
        string temporaryPath = HistoryPath + ".tmp";
        try
        {
            HistoryDocument document = new HistoryDocument { Version = 1, Points = new List<HistoryPointDocument>() };
            foreach (HistoryPoint point in points)
            {
                document.Points.Add(new HistoryPointDocument
                {
                    ProviderId = point.ProviderId,
                    LimitWindowSeconds = point.LimitWindowSeconds,
                    UsedPercent = point.UsedPercent,
                    ResetAt = FormatDate(point.ResetAt),
                    ObservedAt = FormatDate(point.ObservedAt)
                });
            }
            string directory = Path.GetDirectoryName(HistoryPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            Directory.CreateDirectory(directory);
            string json = serializer.Serialize(document);
            using (StreamWriter writer = new StreamWriter(temporaryPath, false, new UTF8Encoding(false)))
            {
                writer.Write(json);
            }
            if (!File.Exists(HistoryPath))
            {
                File.Move(temporaryPath, HistoryPath);
                return;
            }
            try
            {
                File.Replace(temporaryPath, HistoryPath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporaryPath, HistoryPath, true);
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                File.Copy(temporaryPath, HistoryPath, true);
                File.Delete(temporaryPath);
            }
        }
        catch (Exception)
        {
            TryDelete(temporaryPath);
        }
    }

    private static void RemoveDuplicate(IList<HistoryPoint> points, string providerId, int seconds, DateTimeOffset observedAt)
    {
        for (int index = points.Count - 1; index >= 0; index--)
        {
            HistoryPoint point = points[index];
            if (string.Equals(point.ProviderId, providerId, StringComparison.Ordinal) &&
                point.LimitWindowSeconds == seconds && point.ObservedAt == observedAt)
            {
                points.RemoveAt(index);
            }
        }
    }

    private void Trim(IList<HistoryPoint> points, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.AddDays(-RetentionDays);
        for (int index = points.Count - 1; index >= 0; index--)
        {
            if (points[index].ObservedAt < cutoff)
            {
                points.RemoveAt(index);
            }
        }
        while (points.Count > MaxPoints)
        {
            points.RemoveAt(0);
        }
    }

    private static int NormalizeRetentionDays(int days)
    {
        return AppSettings.IsSupportedHistoryRetentionDays(days) ? days : 30;
    }

    private void BackupBrokenFile()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return;
            }
            string backupPath = HistoryPath + ".bak";
            TryDelete(backupPath);
            File.Move(HistoryPath, backupPath);
        }
        catch (Exception)
        {
            // 备份失败不阻止当前查询。
        }
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value.HasValue ? value.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty;
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        DateTimeOffset parsed;
        return TryParseDate(value, out parsed) ? parsed : (DateTimeOffset?)null;
    }

    private static bool TryParseDate(string value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
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
            // 清理失败不覆盖主流程。
        }
    }

    private sealed class HistoryDocument
    {
        public int Version { get; set; }
        public List<HistoryPointDocument> Points { get; set; }
    }

    private sealed class HistoryPointDocument
    {
        public string ProviderId { get; set; }
        public int LimitWindowSeconds { get; set; }
        public double UsedPercent { get; set; }
        public string ResetAt { get; set; }
        public string ObservedAt { get; set; }
    }
}
