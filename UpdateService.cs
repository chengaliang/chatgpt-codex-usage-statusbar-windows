using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

/// <summary>
/// GitHub Release 更新检查结果。结果只保留版本和经过白名单校验的链接，不保存接口原文。
/// </summary>
internal sealed class UpdateCheckResult
{
    public bool IsSuccess { get; private set; }
    public bool IsUpdateAvailable { get; private set; }
    public string LatestVersion { get; private set; }
    public string ReleaseUrl { get; private set; }
    public string DownloadUrl { get; private set; }
    public string AssetName { get; private set; }
    public string Sha256 { get; private set; }
    public string ErrorCode { get; private set; }
    public DateTimeOffset CheckedAt { get; private set; }

    private UpdateCheckResult()
    {
        LatestVersion = string.Empty;
        ReleaseUrl = string.Empty;
        DownloadUrl = string.Empty;
        AssetName = string.Empty;
        Sha256 = string.Empty;
        ErrorCode = string.Empty;
        CheckedAt = DateTimeOffset.Now;
    }

    public static UpdateCheckResult Success(
        bool updateAvailable,
        string latestVersion,
        string releaseUrl,
        string downloadUrl,
        string assetName,
        string sha256)
    {
        UpdateCheckResult result = new UpdateCheckResult();
        result.IsSuccess = true;
        result.IsUpdateAvailable = updateAvailable;
        result.LatestVersion = latestVersion ?? string.Empty;
        result.ReleaseUrl = releaseUrl ?? string.Empty;
        result.DownloadUrl = downloadUrl ?? string.Empty;
        result.AssetName = assetName ?? string.Empty;
        result.Sha256 = sha256 ?? string.Empty;
        return result;
    }

    public static UpdateCheckResult Failure(string errorCode)
    {
        UpdateCheckResult result = new UpdateCheckResult();
        result.ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "update_check_failed" : errorCode;
        return result;
    }
}

/// <summary>
/// 访问本项目公开 Release 元数据并比较版本。更新流程保持人工确认，避免后台覆盖用户正在运行的文件。
/// </summary>
internal sealed class UpdateService : IDisposable
{
    public const string CurrentVersion = "0.6.0";
    private const string ReleaseEndpoint = "https://api.github.com/repos/chengaliang/chatgpt-codex-usage-statusbar-windows/releases/latest";
    private const string ExpectedAssetName = "SubscriptionStatus.exe";
    private readonly HttpClient client;
    private readonly JavaScriptSerializer serializer;
    private bool disposed;

    public UpdateService()
    {
        HttpClientHandler handler = new HttpClientHandler();
        handler.UseProxy = true;
        string proxyAddress = Environment.GetEnvironmentVariable("CLASH_MIXED_PROXY");
        Uri proxyUri;
        if (!string.IsNullOrWhiteSpace(proxyAddress) &&
            Uri.TryCreate(proxyAddress, UriKind.Absolute, out proxyUri) &&
            (proxyUri.Scheme == Uri.UriSchemeHttp || proxyUri.Scheme == Uri.UriSchemeHttps))
        {
            handler.Proxy = new WebProxy(proxyUri);
        }
        client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Codex-UsageStatusBar/" + CurrentVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        serializer = new JavaScriptSerializer();
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken)
    {
        if (disposed)
        {
            return UpdateCheckResult.Failure("update_service_disposed");
        }

        try
        {
            using (HttpResponseMessage response = await client.GetAsync(ReleaseEndpoint, cancellationToken).ConfigureAwait(true))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return UpdateCheckResult.Failure("update_http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                }

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                return ParseRelease(body);
            }
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failure("update_timeout");
        }
        catch (HttpRequestException)
        {
            return UpdateCheckResult.Failure("update_network_unavailable");
        }
        catch (Exception)
        {
            return UpdateCheckResult.Failure("update_parse_failed");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        client.Dispose();
    }

    /// <summary>
    /// 对下载后的本地文件计算 SHA-256。摘要必须是 64 位十六进制，避免把任意文本当作校验结果。
    /// </summary>
    public static bool VerifySha256(string filePath, string expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(expectedDigest) || !File.Exists(filePath))
        {
            return false;
        }

        string expected = expectedDigest.Trim();
        if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            expected = expected.Substring("sha256:".Length);
        }
        if (expected.Length != 64)
        {
            return false;
        }

        foreach (char character in expected)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        try
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = File.OpenRead(filePath))
            {
                byte[] digest = sha256.ComputeHash(stream);
                string actual = BitConverter.ToString(digest).Replace("-", string.Empty).ToLowerInvariant();
                return string.Equals(actual, expected.ToLowerInvariant(), StringComparison.Ordinal);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private UpdateCheckResult ParseRelease(string body)
    {
        Dictionary<string, object> root = serializer.DeserializeObject(body) as Dictionary<string, object>;
        if (root == null)
        {
            return UpdateCheckResult.Failure("update_invalid_response");
        }

        string tag = GetString(root, "tag_name");
        string latestVersion = NormalizeVersion(tag);
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return UpdateCheckResult.Failure("update_missing_version");
        }

        string releaseUrl = SafeGitHubUrl(GetString(root, "html_url"));
        string downloadUrl = string.Empty;
        string assetName = string.Empty;
        string sha256 = string.Empty;
        object[] assets = root.ContainsKey("assets") ? root["assets"] as object[] : null;
        if (assets != null)
        {
            foreach (object value in assets)
            {
                Dictionary<string, object> asset = value as Dictionary<string, object>;
                if (asset == null || !string.Equals(GetString(asset, "name"), ExpectedAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                downloadUrl = SafeGitHubUrl(GetString(asset, "browser_download_url"));
                assetName = ExpectedAssetName;
                sha256 = NormalizeDigest(GetString(asset, "digest"));
                break;
            }
        }

        bool updateAvailable = IsNewer(CurrentVersion, latestVersion);
        return UpdateCheckResult.Success(updateAvailable, latestVersion, releaseUrl, downloadUrl, assetName, sha256);
    }

    private static bool IsNewer(string current, string latest)
    {
        Version currentVersion;
        Version latestVersion;
        if (Version.TryParse(NormalizeVersion(current), out currentVersion) && Version.TryParse(NormalizeVersion(latest), out latestVersion))
        {
            return latestVersion > currentVersion;
        }
        return !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        while (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(1);
        }
        Version parsed;
        return Version.TryParse(normalized, out parsed) ? parsed.ToString() : string.Empty;
    }

    private static string NormalizeDigest(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("sha256:".Length);
        }
        if (normalized.Length != 64)
        {
            return string.Empty;
        }
        foreach (char character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                return string.Empty;
            }
        }
        return normalized.ToLowerInvariant();
    }

    private static string SafeGitHubUrl(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return string.Empty;
        }
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return uri.AbsoluteUri;
    }

    private static string GetString(Dictionary<string, object> source, string key)
    {
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return string.Empty;
        }
        return Convert.ToString(source[key], CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
