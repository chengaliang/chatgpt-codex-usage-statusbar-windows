using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// 单个官方额度窗口。Codex/ChatGPT 后端通常返回 5 小时和 7 天两个动态窗口。
/// </summary>
internal sealed class QuotaWindow
{
    public string Name { get; private set; }
    public int LimitWindowSeconds { get; private set; }
    public double UsedPercent { get; private set; }
    public DateTimeOffset? ResetAt { get; private set; }

    public QuotaWindow(string name, int limitWindowSeconds, double usedPercent, DateTimeOffset? resetAt)
    {
        Name = name;
        LimitWindowSeconds = limitWindowSeconds;
        if (double.IsNaN(usedPercent) || double.IsInfinity(usedPercent))
        {
            usedPercent = 0d;
        }
        UsedPercent = Math.Max(0d, Math.Min(100d, usedPercent));
        ResetAt = resetAt;
    }
}

/// <summary>
/// 状态栏需要的查询结果。错误结果只保留可展示的原因，不携带 OAuth 内容或响应原文。
/// </summary>
internal sealed class QuotaSnapshot
{
    public bool Success { get; private set; }
    public string PlanName { get; private set; }
    public string AccountLabel { get; private set; }
    public string StatusText { get; private set; }
    public string ErrorText { get; private set; }
    public DateTimeOffset? QueriedAt { get; private set; }
    public DateTimeOffset? LastLiveAt { get; private set; }
    public bool IsStale { get; private set; }
    public IList<QuotaWindow> Windows { get; private set; }

    private QuotaSnapshot()
    {
        PlanName = "ChatGPT";
        AccountLabel = "账户未识别";
        StatusText = "读取中";
        ErrorText = string.Empty;
        Windows = new List<QuotaWindow>();
        IsStale = false;
    }

    public static QuotaSnapshot Loading()
    {
        return new QuotaSnapshot();
    }

    public static QuotaSnapshot SuccessResult(string planName, string accountLabel, IList<QuotaWindow> windows)
    {
        QuotaSnapshot result = new QuotaSnapshot();
        result.Success = true;
        result.PlanName = string.IsNullOrWhiteSpace(planName) ? "ChatGPT" : planName;
        result.AccountLabel = string.IsNullOrWhiteSpace(accountLabel) ? "当前账户" : accountLabel;
        result.StatusText = "正常";
        result.QueriedAt = DateTimeOffset.Now;
        result.LastLiveAt = result.QueriedAt;
        result.Windows = windows;
        return result;
    }

    public static QuotaSnapshot CachedResult(
        string planName,
        IList<QuotaWindow> windows,
        DateTimeOffset? lastLiveAt,
        string statusText,
        string errorText)
    {
        QuotaSnapshot result = SuccessResult(planName, "当前账户", windows);
        result.IsStale = true;
        result.LastLiveAt = lastLiveAt;
        result.StatusText = string.IsNullOrWhiteSpace(statusText) ? "缓存" : statusText;
        result.ErrorText = errorText ?? string.Empty;
        return result;
    }

    public static QuotaSnapshot Failure(string statusText, string errorText)
    {
        QuotaSnapshot result = new QuotaSnapshot();
        result.Success = false;
        result.StatusText = statusText;
        result.ErrorText = errorText ?? string.Empty;
        result.QueriedAt = DateTimeOffset.Now;
        return result;
    }
}

/// <summary>
/// 统一处理诊断中可能来自 OAuth 声明的计划名称，避免把未验证的任意文本复制到 Issue。
/// </summary>
internal static class DiagnosticSanitizer
{
    public static string PlanName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ChatGPT";
        }

        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.Equals(normalized, "ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return "ChatGPT";
        }

        // OAuth 声明有时只返回 plus/pro 等短码，有时会返回 GPT Plus 等展示名；
        // 统一去掉前缀和分隔符后再匹配固定白名单，避免把远端任意文本直接显示到界面。
        string planKey = normalized.Replace('_', ' ').Replace('-', ' ').Trim();
        if (planKey.StartsWith("GPT ", StringComparison.OrdinalIgnoreCase))
        {
            planKey = planKey.Substring(4).Trim();
        }

        switch (planKey.ToLowerInvariant())
        {
            case "free":
                return "GPT Free";
            case "go":
                return "GPT Go";
            case "plus":
                return "GPT Plus";
            case "pro":
                return "GPT Pro";
            case "team":
                return "GPT Team";
            case "business":
                return "GPT Business";
            case "enterprise":
                return "GPT Enterprise";
            case "edu":
                return "GPT Edu";
            default:
                return "ChatGPT";
        }
    }
}

/// <summary>
/// 使用 Codex CLI 已有的 OAuth 凭据查询官方 ChatGPT/Codex 额度。
/// 凭据只在内存中使用，不会写回 auth.json，也不会把令牌放入日志或 UI。
/// </summary>
internal sealed class OfficialQuotaService : IDisposable
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";
    private readonly HttpClient client;
    private readonly JavaScriptSerializer serializer;

    public OfficialQuotaService()
    {
        // .NET Framework 可能默认协商到较低 TLS 版本；官方接口要求 TLS 1.2。
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        // 只有用户显式配置代理时才使用指定地址；未配置时交给 Windows 系统代理，
        // 没有系统代理则由 HttpClientHandler 直接连接，避免强制依赖 Clash Verge。
        string proxyAddress = Environment.GetEnvironmentVariable("CLASH_MIXED_PROXY");

        HttpClientHandler handler = new HttpClientHandler();
        handler.UseProxy = true;
        if (!string.IsNullOrWhiteSpace(proxyAddress))
        {
            handler.Proxy = CreateProxy(proxyAddress);
        }
        client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        serializer = new JavaScriptSerializer();
    }

    private static IWebProxy CreateProxy(string proxyAddress)
    {
        Uri proxyUri;
        if (!Uri.TryCreate(proxyAddress, UriKind.Absolute, out proxyUri) ||
            (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            // 环境变量格式错误时回退到 Windows 系统代理，避免构造窗口时直接退出。
            return WebRequest.DefaultWebProxy;
        }
        return new WebProxy(proxyUri);
    }

    public async Task<QuotaSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            CredentialData credentials;
            string credentialError;
            if (!TryReadCredentials(out credentials, out credentialError))
            {
                return QuotaSnapshot.Failure("OAuth 不可用", credentialError);
            }

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                request.Headers.UserAgent.ParseAdd("codex-cli");
                request.Headers.Accept.ParseAdd("application/json");
                if (!string.IsNullOrWhiteSpace(credentials.AccountId))
                {
                    request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credentials.AccountId);
                }

                try
                {
                    using (HttpResponseMessage response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseContentRead,
                        cancellationToken).ConfigureAwait(true))
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden)
                        {
                            return QuotaSnapshot.Failure("OAuth 已过期", "请先在终端运行 codex login");
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            // 不把官方响应体写入状态栏，避免意外展示账户内部信息。
                            return QuotaSnapshot.Failure(
                                "官方接口异常",
                                "额度接口返回 HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                        }

                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                        return ParseResponse(body, credentials);
                    }
                }
                catch (TaskCanceledException)
                {
                    return QuotaSnapshot.Failure("请求超时", "请确认 Clash Verge 正在运行且网络可用");
                }
                catch (HttpRequestException)
                {
                    return QuotaSnapshot.Failure("网络不可用", "请检查系统网络或 CLASH_MIXED_PROXY 配置");
                }
                catch (Exception)
                {
                    // UI 只显示稳定、可操作的提示，不暴露异常堆栈或请求细节。
                    return QuotaSnapshot.Failure("查询失败", "官方额度响应无法解析");
                }
            }
        }
        catch (Exception)
        {
            // 凭据内容或请求头异常时也要转成可展示状态，不能让 async UI 入口崩溃。
            return QuotaSnapshot.Failure("查询失败", "OAuth 凭据或请求参数无效");
        }
    }

    private QuotaSnapshot ParseResponse(string body, CredentialData credentials)
    {
        Dictionary<string, object> root = serializer.DeserializeObject(body) as Dictionary<string, object>;
        if (root == null)
        {
            return QuotaSnapshot.Failure("查询失败", "官方额度响应不是 JSON 对象");
        }

        Dictionary<string, object> rateLimit = GetDictionary(root, "rate_limit");
        if (rateLimit == null)
        {
            return QuotaSnapshot.Failure("暂无额度数据", "官方响应没有 rate_limit");
        }

        List<QuotaWindow> windows = new List<QuotaWindow>();
        AddWindow(rateLimit, "primary_window", windows);
        AddWindow(rateLimit, "secondary_window", windows);
        windows.Sort(delegate(QuotaWindow left, QuotaWindow right)
        {
            return left.LimitWindowSeconds.CompareTo(right.LimitWindowSeconds);
        });

        if (windows.Count == 0)
        {
            return QuotaSnapshot.Failure("暂无额度数据", "官方响应没有可用窗口");
        }

        return QuotaSnapshot.SuccessResult(credentials.PlanName, credentials.AccountLabel, windows);
    }

    private void AddWindow(Dictionary<string, object> rateLimit, string key, IList<QuotaWindow> windows)
    {
        Dictionary<string, object> data = GetDictionary(rateLimit, key);
        if (data == null)
        {
            return;
        }

        double usedPercent;
        if (!TryGetDouble(data, "used_percent", out usedPercent))
        {
            return;
        }

        long windowSeconds;
        TryGetLong(data, "limit_window_seconds", out windowSeconds);
        long resetSeconds;
        DateTimeOffset? resetAt = null;
        if (TryGetLong(data, "reset_at", out resetSeconds) && resetSeconds > 0)
        {
            try
            {
                // 用 Unix epoch 手动换算，兼容 .NET Framework 4.5，避免依赖 4.6 新增 API。
                resetAt = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(resetSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                resetAt = null;
            }
        }

        windows.Add(new QuotaWindow(
            GetWindowName(windowSeconds),
            (int)Math.Max(0L, Math.Min(Int32.MaxValue, windowSeconds)),
            usedPercent,
            resetAt));
    }

    /// <summary>
    /// 生成不含令牌、账户 ID 和响应原文的凭据诊断摘要，供用户提交 Issue 时参考。
    /// </summary>
    public string GetCredentialDiagnostic()
    {
        CredentialData credentials;
        string error;
        if (!TryReadCredentials(out credentials, out error))
        {
            return "OAuth：不可用\r\n原因：" + error;
        }

        return "OAuth：ChatGPT OAuth 配置可读取\r\n计划：" + DiagnosticSanitizer.PlanName(credentials.PlanName);
    }

    /// <summary>
    /// 返回代理模式摘要。为避免泄露内网地址，只报告模式和协议，不返回环境变量原值。
    /// </summary>
    public string GetProxyDiagnostic()
    {
        string proxyAddress = Environment.GetEnvironmentVariable("CLASH_MIXED_PROXY");
        if (string.IsNullOrWhiteSpace(proxyAddress))
        {
            return "网络：Windows 系统代理或直连";
        }

        Uri proxyUri;
        if (!Uri.TryCreate(proxyAddress, UriKind.Absolute, out proxyUri) ||
            (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps))
        {
            return "网络：自定义代理格式无效，已回退系统代理";
        }

        return "网络：自定义 " + proxyUri.Scheme.ToUpperInvariant() + " 代理（地址已隐藏）";
    }

    private static string GetWindowName(long seconds)
    {
        if (seconds == 18000)
        {
            return "5 小时窗口";
        }
        if (seconds == 604800)
        {
            return "7 天窗口";
        }
        if (seconds >= 86400)
        {
            return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + " 天窗口";
        }
        if (seconds >= 3600)
        {
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " 小时窗口";
        }
        return "额度窗口";
    }

    private bool TryReadCredentials(out CredentialData credentials, out string error)
    {
        credentials = null;
        error = string.Empty;
        string authPath;
        try
        {
            string codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome))
            {
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }

            authPath = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(authPath))
            {
                error = "找不到 Codex CLI 凭据文件";
                return false;
            }
        }
        catch (Exception)
        {
            error = "Codex CLI 凭据路径不可访问";
            return false;
        }

        try
        {
            string authText = File.ReadAllText(authPath);
            Dictionary<string, object> auth = serializer.DeserializeObject(authText) as Dictionary<string, object>;
            if (auth == null || !string.Equals(GetString(auth, "auth_mode"), "chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                error = "Codex 当前不是 ChatGPT OAuth 模式";
                return false;
            }

            Dictionary<string, object> tokens = GetDictionary(auth, "tokens");
            string accessToken = GetString(tokens, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                error = "auth.json 中没有 access_token";
                return false;
            }

            string accountId = GetString(tokens, "account_id");
            string planName = ResolvePlanName(tokens);
            credentials = new CredentialData(accessToken, accountId, planName, MaskAccountId(accountId));
            return true;
        }
        catch (Exception)
        {
            error = "auth.json 无法解析";
            return false;
        }
    }

    private string ResolvePlanName(Dictionary<string, object> tokens)
    {
        // Codex CLI 的不同版本会把套餐声明放在 id_token 的顶层或官方 auth 对象中；
        // 先读 id_token，再用 access_token 作为兼容回退，不读取或记录令牌本身。
        string[] tokenKeys = { "id_token", "access_token" };
        foreach (string tokenKey in tokenKeys)
        {
            string token = GetString(tokens, tokenKey);
            string planType = ResolvePlanType(token);
            string planName = DiagnosticSanitizer.PlanName(planType);
            if (!string.Equals(planName, "ChatGPT", StringComparison.Ordinal))
            {
                return planName;
            }
        }

        return "ChatGPT";
    }

    private string ResolvePlanType(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        string[] segments = token.Split('.');
        if (segments.Length < 2)
        {
            return string.Empty;
        }

        try
        {
            string payload = DecodeBase64Url(segments[1]);
            Dictionary<string, object> claims = serializer.DeserializeObject(payload) as Dictionary<string, object>;
            if (claims == null)
            {
                return string.Empty;
            }

            string planType = GetString(claims, "https://api.openai.com/auth.chatgpt_plan_type");
            if (string.IsNullOrWhiteSpace(planType))
            {
                planType = GetString(claims, "chatgpt_plan_type");
            }

            // 当前 Codex OAuth 使用 https://api.openai.com/auth 对象承载套餐和订阅声明。
            Dictionary<string, object> authClaims = GetDictionary(claims, "https://api.openai.com/auth");
            if (string.IsNullOrWhiteSpace(planType))
            {
                planType = GetString(authClaims, "chatgpt_plan_type");
            }

            // 保留 profile 作为旧版/未来 token 的兼容位置，但仍只接受白名单计划。
            Dictionary<string, object> profileClaims = GetDictionary(claims, "https://api.openai.com/profile");
            if (string.IsNullOrWhiteSpace(planType))
            {
                planType = GetString(profileClaims, "chatgpt_plan_type");
            }

            return planType;
        }
        catch (Exception)
        {
            // 计划声明只用于标题，解析失败时使用安全的通用名称。
            return string.Empty;
        }
    }

    private static string DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        while (padded.Length % 4 != 0)
        {
            padded += "=";
        }
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string MaskAccountId(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return "当前账户";
        }
        if (accountId.Length <= 4)
        {
            return "账户 ····" + accountId;
        }
        return "账户 ····" + accountId.Substring(accountId.Length - 4);
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
    {
        if (source == null || !source.ContainsKey(key))
        {
            return null;
        }
        return source[key] as Dictionary<string, object>;
    }

    private static string GetString(Dictionary<string, object> source, string key)
    {
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return string.Empty;
        }
        return Convert.ToString(source[key], CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetDouble(Dictionary<string, object> source, string key, out double value)
    {
        value = 0d;
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return false;
        }
        try
        {
            value = Convert.ToDouble(source[key], CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                value = 0d;
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetLong(Dictionary<string, object> source, string key, out long value)
    {
        value = 0L;
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return false;
        }
        try
        {
            value = Convert.ToInt64(source[key], CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        client.Dispose();
    }

    private sealed class CredentialData
    {
        public string AccessToken { get; private set; }
        public string AccountId { get; private set; }
        public string PlanName { get; private set; }
        public string AccountLabel { get; private set; }

        public CredentialData(string accessToken, string accountId, string planName, string accountLabel)
        {
            AccessToken = accessToken;
            AccountId = accountId;
            PlanName = planName;
            AccountLabel = accountLabel;
        }
    }
}

/// <summary>
/// ChatGPT/Codex 迷你额度状态栏。常驻窗口只显示关键数字，账户和错误详情通过悬停提示查看。
/// </summary>
internal sealed class StatusWindow : Form
{
    private const int WindowWidth = 370;
    private const int WindowHeight = 56;
    private const string ProjectUrl = "https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows";
    private readonly Rectangle closeArea = new Rectangle(WindowWidth - 30, 17, 20, 20);
    private readonly Rectangle refreshArea = new Rectangle(WindowWidth - 58, 17, 20, 20);
    private readonly Rectangle expandArea = new Rectangle(WindowWidth - 86, 17, 20, 20);
    private readonly IUsageProvider usageProvider;
    private readonly SettingsStore settingsStore;
    private readonly AppSettings settings;
    private readonly RefreshScheduler refreshScheduler;
    private readonly CancellationTokenSource cancellation;
    private readonly ToolTip toolTip;
    private readonly ContextMenuStrip contextMenu;
    private readonly StartupManager startupManager;
    private readonly TrayController trayController;
    private readonly NotificationEvaluator notificationEvaluator;
    private readonly DiagnosticsService diagnosticsService;
    private readonly UsageCache usageCache;
    private readonly HistoryStore historyStore;
    private readonly UpdateService updateService;
    private readonly GlobalHotkey globalHotkey;
    private ThemePalette themePalette;
    private ToolStripMenuItem autoStartMenuItem;
    private ToolStripMenuItem clickThroughMenuItem;
    private readonly IList<ToolStripMenuItem> refreshIntervalItems = new List<ToolStripMenuItem>();
    private readonly IList<ToolStripMenuItem> backgroundStyleItems = new List<ToolStripMenuItem>();
    private QuotaSnapshot snapshot;
    private UsageSnapshot usageSnapshot;
    private bool isRefreshing;
    private bool userMoved;
    private bool autoStartEnabled;
    private string autoStartError;
    private bool exitRequested;
    private bool startupUpdateCheckCompleted;
    private bool globalHotkeyRegistrationFailed;
    private UsageHubForm usageHubForm;
    private Task refreshInFlight;
    private Task updateCheckInFlight;
    private readonly System.Windows.Forms.Timer visualTimer;
    private double animatedPrimaryPercent;
    private double animatedSecondaryPercent;
    private double targetPrimaryPercent;
    private double targetSecondaryPercent;
    private DateTime nextResetPaintAt;
    private float visualPhase;
    private int refreshRotation;
    private bool visualAnimationActive;
    private float refreshCelebrationProgress;
    private bool refreshCelebrationActive;
    private Point mouseDownLocation;
    private bool draggingBar;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out NativeRect rectangle, uint update);

    private const uint SpiGetWorkArea = 0x0030;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;

    public StatusWindow()
    {
        Text = "ChatGPT quota";
        ClientSize = new Size(WindowWidth, WindowHeight);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(13, 17, 23);
        DoubleBuffered = true;
        // 固定像素尺寸，避免 DPI 自动缩放后按钮和圆角错位。
        AutoScaleMode = AutoScaleMode.None;
        using (GraphicsPath regionPath = RoundedRectangle(new Rectangle(0, 0, WindowWidth, WindowHeight), 9))
        {
            Region = new Region(regionPath);
        }

        usageProvider = new OfficialUsageProvider();
        settingsStore = new SettingsStore();
        settings = settingsStore.Load();
        cancellation = new CancellationTokenSource();
        usageCache = new UsageCache();
        historyStore = new HistoryStore(settings.HistoryRetentionDays);
        updateService = new UpdateService();
        usageSnapshot = usageCache.Load();
        if (usageSnapshot == null)
        {
            usageSnapshot = UsageSnapshot.Loading("chatgpt-codex");
            snapshot = QuotaSnapshot.Loading();
        }
        else
        {
            snapshot = usageSnapshot.ToQuotaSnapshot();
        }
        notificationEvaluator = new NotificationEvaluator();
        diagnosticsService = new DiagnosticsService();
        startupManager = new StartupManager(Application.ExecutablePath);
        autoStartError = string.Empty;
        bool startupEnabled;
        string startupError;
        if (!startupManager.TryGetEnabled(out startupEnabled, out startupError))
        {
            autoStartEnabled = false;
            autoStartError = startupError;
        }
        else
        {
            autoStartEnabled = startupEnabled;
        }
        settings.AutoStartEnabled = autoStartEnabled;
        themePalette = ThemePalette.Create(settings.Theme);
        visualTimer = new System.Windows.Forms.Timer();
        visualTimer.Interval = settings.AnimationsEnabled ? 33 : 1000;
        visualTimer.Tick += VisualTimerTick;
        UpdateVisualTargets(false);
        nextResetPaintAt = DateTime.UtcNow;
        visualTimer.Start();
        ApplyTheme();
        ApplyBackgroundStyle();
        contextMenu = CreateContextMenu();
        ContextMenuStrip = contextMenu;
        toolTip = new ToolTip();
        toolTip.AutoPopDelay = 8000;
        toolTip.InitialDelay = 350;
        toolTip.ReshowDelay = 100;
        globalHotkey = new GlobalHotkey(delegate
        {
            // WM_HOTKEY 位于消息循环内，投递异步消息避免在 NativeWindow 回调中嵌套 ShowDialog。
            if (!IsDisposed && IsHandleCreated)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate { ShowUsageHub(); });
                }
                catch (InvalidOperationException)
                {
                    // 主窗口正在销毁时，异步投递可能失去句柄；此时无需再次打开工作区。
                }
            }
        });
        toolTip.SetToolTip(this, BuildTooltipText());

        refreshScheduler = new RefreshScheduler(settings.RefreshIntervalMinutes, RefreshQuotaSafelyAsync);
        refreshScheduler.Start();
        trayController = new TrayController(
            ShowFromTray,
            ShowUsageHub,
            RefreshFromTray,
            ShowSettings,
            RunDiagnostics,
            OpenProjectPage,
            ExitApplication,
            delegate { return settings.ClickThroughEnabled; },
            ApplyClickThrough);
        trayController.ApplyTheme(themePalette);
        trayController.SetClickThroughEnabled(settings.ClickThroughEnabled);
    }

    /// <summary>
    /// 构造右键菜单。状态栏保持极简，设置、诊断和项目入口集中在这里。
    /// </summary>
    private ContextMenuStrip CreateContextMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;

        ToolStripMenuItem hubItem = new ToolStripMenuItem("打开 Usage Hub");
        hubItem.Font = new Font(UiTheme.CjkFontFamily, 9f, FontStyle.Bold);
        hubItem.Click += delegate(object sender, EventArgs args) { ShowUsageHub(); };

        ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
        refreshItem.Click += async delegate(object sender, EventArgs args) { await RefreshQuotaSafelyAsync(); };

        ToolStripMenuItem intervalMenu = new ToolStripMenuItem("刷新周期");
        foreach (int minutes in AppSettings.GetSupportedRefreshIntervals())
        {
            ToolStripMenuItem intervalItem = new ToolStripMenuItem(minutes + " 分钟");
            intervalItem.Tag = minutes;
            intervalItem.Checked = settings.RefreshIntervalMinutes == minutes;
            intervalItem.Click += delegate(object sender, EventArgs args)
            {
                ToolStripMenuItem selected = sender as ToolStripMenuItem;
                if (selected != null)
                {
                    ApplyRefreshInterval((int)selected.Tag);
                }
            };
            refreshIntervalItems.Add(intervalItem);
            intervalMenu.DropDownItems.Add(intervalItem);
        }

        ToolStripMenuItem styleMenu = new ToolStripMenuItem("背景样式");
        AddBackgroundStyleItem(styleMenu, BackgroundStyle.Opaque, "实色");
        AddBackgroundStyleItem(styleMenu, BackgroundStyle.SemiTransparent, "半透明");
        AddBackgroundStyleItem(styleMenu, BackgroundStyle.HighTransparency, "高透明");
        AddBackgroundStyleItem(styleMenu, BackgroundStyle.UltraTransparency, "极高透明");

        clickThroughMenuItem = new ToolStripMenuItem("忽略鼠标操作（点击穿透）");
        clickThroughMenuItem.CheckOnClick = true;
        clickThroughMenuItem.Checked = settings.ClickThroughEnabled;
        clickThroughMenuItem.Click += delegate(object sender, EventArgs args)
        {
            ApplyClickThrough(clickThroughMenuItem.Checked);
        };

        autoStartMenuItem = new ToolStripMenuItem("开机自启");
        autoStartMenuItem.CheckOnClick = true;
        autoStartMenuItem.Checked = autoStartEnabled;
        autoStartMenuItem.Click += delegate(object sender, EventArgs args)
        {
            bool requested = autoStartMenuItem.Checked;
            string startupError;
            if (!startupManager.TrySetEnabled(requested, out startupError))
            {
                autoStartMenuItem.Checked = autoStartEnabled;
                autoStartError = startupError;
                MessageBox.Show(this, autoStartError, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                autoStartEnabled = requested;
                autoStartError = string.Empty;
                settings.AutoStartEnabled = requested;
                SaveSettings();
            }
        };

        ToolStripMenuItem diagnosticsItem = new ToolStripMenuItem("诊断中心");
        diagnosticsItem.Click += RunDiagnostics;

        ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += delegate(object sender, EventArgs args) { ShowSettings(); };

        ToolStripMenuItem copyDiagnosticsItem = new ToolStripMenuItem("复制诊断信息");
        copyDiagnosticsItem.Click += delegate(object sender, EventArgs args) { CopyDiagnosticReport(); };

        ToolStripMenuItem exportHistoryItem = new ToolStripMenuItem("导出本地趋势");
        exportHistoryItem.Click += delegate(object sender, EventArgs args) { ExportHistoryCsv(); };

        ToolStripMenuItem dataFolderItem = new ToolStripMenuItem("打开数据目录");
        dataFolderItem.Click += delegate(object sender, EventArgs args) { OpenDataFolder(); };

        ToolStripMenuItem clearHistoryItem = new ToolStripMenuItem("清除趋势历史与导出");
        clearHistoryItem.Click += delegate(object sender, EventArgs args) { ClearHistoryData(); };

        ToolStripMenuItem clearCacheItem = new ToolStripMenuItem("清除最近成功缓存");
        clearCacheItem.Click += delegate(object sender, EventArgs args) { ClearCacheData(); };

        ToolStripMenuItem projectItem = new ToolStripMenuItem("打开项目主页");
        projectItem.Click += delegate(object sender, EventArgs args) { OpenProjectPage(); };

        ToolStripMenuItem updateItem = new ToolStripMenuItem("检查更新");
        updateItem.Click += async delegate(object sender, EventArgs args)
        {
            await CheckForUpdatesAsync(sender as ToolStripMenuItem);
        };

        ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += delegate(object sender, EventArgs args) { ExitApplication(); };

        menu.Items.Add(hubItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(intervalMenu);
        menu.Items.Add(styleMenu);
        menu.Items.Add(clickThroughMenuItem);
        menu.Items.Add(autoStartMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(copyDiagnosticsItem);
        menu.Items.Add(exportHistoryItem);
        menu.Items.Add(dataFolderItem);
        menu.Items.Add(clearHistoryItem);
        menu.Items.Add(clearCacheItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(projectItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(exitItem);
        UiTheme.StyleMenu(menu, themePalette);
        return menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CS_DROPSHADOW;
            // 工具窗口不进入 Alt+Tab；展示模式额外叠加点击穿透和不抢焦点样式。
            parameters.ExStyle |= WS_EX_TOOLWINDOW;
            if (settings != null && settings.ClickThroughEnabled)
            {
                // 分层窗口是跨进程命中穿透的基础；实色模式也必须显式加入该样式。
                parameters.ExStyle |= WsExLayered | WsExTransparent | WsExNoActivate;
            }
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (settings != null && settings.ClickThroughEnabled)
        {
            if (message.Msg == WmNcHitTest)
            {
                // HTTRANSPARENT 让系统把状态栏区域的鼠标命中交给下方窗口处理。
                message.Result = new IntPtr(HtTransparent);
                return;
            }
            if (message.Msg == WmMouseActivate)
            {
                message.Result = new IntPtr(MaNoActivate);
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // 延后一轮消息再定位，确保无边框窗口完成句柄创建和初次布局。
            BeginInvoke((MethodInvoker)delegate { PositionMiniWindow(); });
            if (settings.AnimationsEnabled)
            {
                visualAnimationActive = true;
                UpdateVisualTimerInterval();
            }
            ApplyGlobalHotkey();
            ApplyClickThroughMode();
            toolTip.SetToolTip(this, BuildTooltipText());
            if (settings.LaunchDelaySeconds > 0)
            {
                // 开机自启时允许代理、网络和 Codex CLI 完成初始化，同时先保留可用的缓存画面。
                await Task.Delay(settings.LaunchDelaySeconds * 1000, cancellation.Token);
            }
            await RefreshQuotaAsync();
            await CheckUpdatesOnStartupAsync();
        }
        catch (Exception)
        {
            // OnShown 属于 async void 入口，最后一层兜底避免异常直接终止消息循环。
            if (!cancellation.IsCancellationRequested)
            {
                isRefreshing = false;
                ApplyQueryFailure(UsageStatus.UnknownError, "startup_failed");
                toolTip.SetToolTip(this, BuildTooltipText());
                trayController.SetStatus(BuildTrayStatus());
                Invalidate();
            }
        }
    }

    private void PositionMiniWindow()
    {
        if (userMoved || !IsHandleCreated)
        {
            return;
        }

        if (settings.RestorePosition && settings.HasSavedPosition && IsSavedPositionVisible())
        {
            SetWindowPos(Handle, IntPtr.Zero, settings.PositionX, settings.PositionY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            return;
        }

        // 以鼠标所在显示器为默认目标，避免多屏用户每次启动都跳回主屏；保存的位置优先级更高。
        Screen targetScreen = Screen.FromPoint(Cursor.Position);
        Rectangle workArea = targetScreen == null ? Screen.PrimaryScreen.WorkingArea : targetScreen.WorkingArea;
        int left = Math.Max(workArea.Left + 4, workArea.Right - Width - 16);
        int top = Math.Max(workArea.Top + 4, workArea.Bottom - Height - 16);
        SetWindowPos(Handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (exitRequested && usageHubForm != null && !usageHubForm.IsDisposed)
        {
            usageHubForm.Close();
        }
        if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        RememberPosition();
        refreshScheduler.Stop();
        refreshScheduler.Dispose();
        visualTimer.Stop();
        visualTimer.Dispose();
        cancellation.Cancel();
        usageProvider.Dispose();
        updateService.Dispose();
        globalHotkey.Dispose();
        cancellation.Dispose();
        toolTip.Dispose();
        contextMenu.Dispose();
        trayController.Dispose();
        base.OnFormClosed(e);
    }

    private void HideToTray()
    {
        RememberPosition();
        Hide();
        visualTimer.Stop();
        visualAnimationActive = false;
        // 收起到托盘意味着用户暂时不在看状态栏，丢弃未展示完的庆典，避免下次显示时播放过期反馈。
        refreshCelebrationActive = false;
        refreshCelebrationProgress = 0f;
        trayController.SetStatus(BuildTrayStatus());
    }

    private bool IsSavedPositionVisible()
    {
        Rectangle saved = new Rectangle(settings.PositionX, settings.PositionY, Width, Height);
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle visiblePart = Rectangle.Intersect(saved, screen.WorkingArea);
            if (visiblePart.Width >= Math.Min(Width, 80) && visiblePart.Height >= Math.Min(Height, 20))
            {
                return true;
            }
        }
        return false;
    }

    private void RememberPosition()
    {
        if (!settings.RestorePosition || !IsHandleCreated || WindowState != FormWindowState.Normal)
        {
            return;
        }

        settings.HasSavedPosition = true;
        settings.PositionX = Left;
        settings.PositionY = Top;
        string error;
        settingsStore.TrySave(settings, out error);
    }

    private void ShowFromTray()
    {
        if (IsDisposed)
        {
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        if (settings.AnimationsEnabled)
        {
            visualAnimationActive = true;
            UpdateVisualTimerInterval();
        }
        visualTimer.Start();
        trayController.SetStatus(BuildTrayStatus());
    }

    private void ExitApplication()
    {
        exitRequested = true;
        Close();
    }

    /// <summary>
    /// 根据本地偏好注册或释放全局快捷键。注册失败只保留诊断状态，不影响托盘和手动入口。
    /// </summary>
    private void ApplyGlobalHotkey()
    {
        if (settings.GlobalHotkeyEnabled)
        {
            globalHotkeyRegistrationFailed = !globalHotkey.TryRegister();
            return;
        }
        globalHotkey.Unregister();
        globalHotkeyRegistrationFailed = false;
    }

    private async void RefreshFromTray()
    {
        await RefreshQuotaSafelyAsync();
    }

    /// <summary>
    /// 打开可展开的 Usage Hub。工作区使用当前脱敏快照和本地趋势，所有刷新仍复用主窗口的 Provider 链路。
    /// </summary>
    private void ShowUsageHub()
    {
        if (usageHubForm != null && !usageHubForm.IsDisposed)
        {
            if (usageHubForm.WindowState == FormWindowState.Minimized)
            {
                usageHubForm.WindowState = FormWindowState.Normal;
            }
            usageHubForm.Activate();
            return;
        }

        UsageHubForm form = new UsageHubForm(
            usageSnapshot,
            historyStore.Load(),
            RefreshForDetailsAsync,
            delegate { return historyStore.Load(); },
            ShowSettings,
            ShowDiagnosticReport,
            OpenProjectPage,
            CopyDiagnosticReport,
            ExportHistoryCsv,
            settings.Theme,
            settings.AnimationsEnabled);
        usageHubForm = form;
        try
        {
            form.ShowDialog(this);
        }
        finally
        {
            if (ReferenceEquals(usageHubForm, form))
            {
                usageHubForm = null;
            }
            form.Dispose();
        }
    }

    /// <summary>
    /// 保留旧的内部入口名称，右键、双击和旧测试都统一进入新的大屏工作区。
    /// </summary>
    private void ShowDetails()
    {
        ShowUsageHub();
    }

    private async Task<UsageSnapshot> RefreshForDetailsAsync()
    {
        await RefreshQuotaSafelyAsync();
        return usageSnapshot == null ? UsageSnapshot.Loading("chatgpt-codex") : usageSnapshot.Clone();
    }

    private string BuildTrayStatus()
    {
        if (snapshot == null || !snapshot.Success)
        {
            return "ChatGPT/Codex 额度状态栏 · 等待刷新";
        }
        if (snapshot.IsStale)
        {
            return "ChatGPT/Codex 额度状态栏 · 使用缓存";
        }
        return "ChatGPT/Codex 额度状态栏 · " + (isRefreshing ? "刷新中" : "状态正常");
    }

    private void ShowSettings()
    {
        using (SettingsForm form = new SettingsForm(settings))
        {
            if (form.ShowDialog(this) != DialogResult.OK || form.Result == null)
            {
                return;
            }

            AppSettings selected = form.Result;
            string startupError;
            if (!startupManager.TrySetEnabled(selected.AutoStartEnabled, out startupError))
            {
                MessageBox.Show(this, startupError, "开机启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            CopySettings(selected);
            autoStartEnabled = selected.AutoStartEnabled;
            autoStartError = string.Empty;
            UpdateVisualTargets(settings.AnimationsEnabled);
            ApplyClickThroughMode();
            clickThroughMenuItem.Checked = settings.ClickThroughEnabled;
            trayController.SetClickThroughEnabled(settings.ClickThroughEnabled);
            autoStartMenuItem.Checked = autoStartEnabled;
            refreshScheduler.SetInterval(settings.RefreshIntervalMinutes);
            UpdateRefreshIntervalChecks();
            UpdateBackgroundStyleChecks();
            ApplyBackgroundStyle();
            ApplyTheme();
            UiTheme.StyleMenu(contextMenu, themePalette);
            trayController.ApplyTheme(themePalette);
            ApplyGlobalHotkey();
            SaveSettings();
            toolTip.SetToolTip(this, BuildTooltipText());
            trayController.SetStatus(BuildTrayStatus());
            Invalidate();
        }
    }

    private void CopySettings(AppSettings source)
    {
        settings.RefreshIntervalMinutes = source.RefreshIntervalMinutes;
        settings.HistoryRetentionDays = source.HistoryRetentionDays;
        settings.AutoStartEnabled = source.AutoStartEnabled;
        settings.LaunchDelaySeconds = source.LaunchDelaySeconds;
        settings.AutoCheckUpdates = source.AutoCheckUpdates;
        settings.BackgroundStyle = source.BackgroundStyle;
        settings.ClickThroughEnabled = source.ClickThroughEnabled;
        settings.Theme = source.Theme;
        settings.NotificationsEnabled = source.NotificationsEnabled;
        settings.NotificationThresholdPercent = source.NotificationThresholdPercent;
        settings.RestorePosition = source.RestorePosition;
        settings.AnimationsEnabled = source.AnimationsEnabled;
        settings.GlobalHotkeyEnabled = source.GlobalHotkeyEnabled;
        settings.ResetNotificationsEnabled = source.ResetNotificationsEnabled;
        settings.ForecastNotificationsEnabled = source.ForecastNotificationsEnabled;
        settings.HasSavedPosition = source.HasSavedPosition;
        settings.PositionX = source.PositionX;
        settings.PositionY = source.PositionY;
        settings.Normalize();
        historyStore.SetRetentionDays(settings.HistoryRetentionDays);
    }

    private void AddBackgroundStyleItem(ToolStripMenuItem parent, BackgroundStyle style, string text)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.Tag = style;
        item.Checked = settings.BackgroundStyle == style;
        item.Click += delegate(object sender, EventArgs args)
        {
            ToolStripMenuItem selected = sender as ToolStripMenuItem;
            if (selected != null)
            {
                ApplyBackgroundStyle((BackgroundStyle)selected.Tag);
            }
        };
        backgroundStyleItems.Add(item);
        parent.DropDownItems.Add(item);
    }

    private void ApplyRefreshInterval(int minutes)
    {
        if (!AppSettings.IsSupportedRefreshInterval(minutes))
        {
            return;
        }

        settings.RefreshIntervalMinutes = minutes;
        settings.Normalize();
        refreshScheduler.SetInterval(settings.RefreshIntervalMinutes);
        UpdateRefreshIntervalChecks();
        SaveSettings();
    }

    private void ApplyBackgroundStyle(BackgroundStyle style)
    {
        settings.BackgroundStyle = style;
        settings.Normalize();
        ApplyBackgroundStyle();
        UpdateBackgroundStyleChecks();
        SaveSettings();
        Invalidate();
    }

    private void ApplyBackgroundStyle()
    {
        switch (settings.BackgroundStyle)
        {
            case BackgroundStyle.SemiTransparent:
                Opacity = 0.85d;
                break;
            case BackgroundStyle.HighTransparency:
                Opacity = 0.65d;
                break;
            case BackgroundStyle.UltraTransparency:
                Opacity = 0.35d;
                break;
            default:
                Opacity = 1.0d;
                break;
        }
    }

    /// <summary>
    /// 切换状态栏展示模式。开启后窗口保留绘制和刷新，但鼠标命中会穿透到后方应用；
    /// 关闭入口保留在状态栏右键菜单和托盘菜单，避免用户把自己锁在不可交互状态。
    /// </summary>
    private void ApplyClickThrough(bool enabled)
    {
        settings.ClickThroughEnabled = enabled;
        settings.Normalize();
        ApplyClickThroughMode();
        if (clickThroughMenuItem != null)
        {
            clickThroughMenuItem.Checked = settings.ClickThroughEnabled;
        }
        if (trayController != null)
        {
            trayController.SetClickThroughEnabled(settings.ClickThroughEnabled);
        }
        SaveSettings();
        toolTip.SetToolTip(this, BuildTooltipText());
        trayController.SetStatus(BuildTrayStatus());
        Invalidate();
    }

    private void ApplyClickThroughMode()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        long extendedStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        if (settings.ClickThroughEnabled)
        {
            // 即使当前背景是不透明的，也要保持 WS_EX_LAYERED，保证跨进程窗口收到实际点击。
            extendedStyle |= WsExLayered | WsExTransparent | WsExNoActivate;
        }
        else
        {
            extendedStyle &= ~(long)(WsExTransparent | WsExNoActivate);
            if (settings.BackgroundStyle == BackgroundStyle.Opaque)
            {
                // 不透明的普通交互模式不需要继续占用分层窗口路径；半透明模式则必须保留它。
                extendedStyle &= ~(long)WsExLayered;
            }
        }

        SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(extendedStyle));
        SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void ApplyTheme()
    {
        themePalette = ThemePalette.Create(settings.Theme);
        BackColor = themePalette.BackgroundTop;
        Invalidate();
    }

    private void UpdateRefreshIntervalChecks()
    {
        foreach (ToolStripMenuItem item in refreshIntervalItems)
        {
            item.Checked = (int)item.Tag == settings.RefreshIntervalMinutes;
        }
    }

    private void UpdateBackgroundStyleChecks()
    {
        foreach (ToolStripMenuItem item in backgroundStyleItems)
        {
            item.Checked = (BackgroundStyle)item.Tag == settings.BackgroundStyle;
        }
    }

    private void SaveSettings()
    {
        string error;
        if (!settingsStore.TrySave(settings, out error) && !string.IsNullOrWhiteSpace(error))
        {
            MessageBox.Show(this, error, "保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private Task RefreshQuotaAsync()
    {
        if (cancellation.IsCancellationRequested)
        {
            return Task.FromResult(0);
        }

        if (refreshInFlight != null && !refreshInFlight.IsCompleted)
        {
            return refreshInFlight;
        }

        refreshInFlight = RefreshQuotaCoreAsync();
        return refreshInFlight;
    }

    private async Task RefreshQuotaCoreAsync()
    {
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        isRefreshing = true;
        BeginVisualRefresh();
        Invalidate();
        try
        {
            UsageSnapshot result = await usageProvider.GetUsageAsync(cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                ApplyUsageResult(result);
            }
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                ApplyQueryFailure(UsageStatus.UnknownError, "refresh_failed");
            }
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                isRefreshing = false;
                toolTip.SetToolTip(this, BuildTooltipText());
                trayController.SetStatus(BuildTrayStatus());
                Invalidate();
                // 某些无边框窗口管理器会在异步首帧后重置位置，查询完成后再校正一次。
                PositionMiniWindow();
            }
        }
    }

    /// <summary>
    /// 按用户显式开启的偏好在首次查询后检查一次公开 Release；默认关闭且不自动下载或替换文件。
    /// </summary>
    private async Task CheckUpdatesOnStartupAsync()
    {
        if (startupUpdateCheckCompleted || !settings.AutoCheckUpdates || cancellation.IsCancellationRequested)
        {
            return;
        }

        startupUpdateCheckCompleted = true;
        try
        {
            await Task.Delay(1500, cancellation.Token);
            await CheckForUpdatesAsync(null);
        }
        catch (TaskCanceledException)
        {
            // 退出时取消延迟属于正常关闭路径，不需要弹出错误。
        }
        catch (Exception)
        {
            // 自动检查失败不应干扰状态栏常驻；用户仍可从右键菜单手动检查。
        }
    }

    /// <summary>
    /// 为定时器、按钮和右键菜单提供统一的异步刷新边界，防止 UI 事件中的未观察异常终止消息循环。
    /// </summary>
    private async Task RefreshQuotaSafelyAsync()
    {
        try
        {
            await RefreshQuotaAsync();
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                isRefreshing = false;
                ApplyQueryFailure(UsageStatus.UnknownError, "refresh_failed");
                toolTip.SetToolTip(this, BuildTooltipText());
                trayController.SetStatus(BuildTrayStatus());
                Invalidate();
            }
        }
    }

    /// <summary>
    /// 启动一次轻量视觉反馈。刷新图标旋转、状态点呼吸和进度条过渡共用同一个 WinForms 定时器，
    /// 避免为每个控件创建独立线程；关闭动效后会立即切换为静态绘制。
    /// </summary>
    private void BeginVisualRefresh()
    {
        CancelRefreshCelebration();
        if (!settings.AnimationsEnabled)
        {
            visualAnimationActive = false;
            UpdateVisualTimerInterval();
            return;
        }

        visualAnimationActive = true;
        visualPhase = 0f;
        refreshRotation = 0;
        UpdateVisualTimerInterval();
    }

    /// <summary>
    /// 根据当前两个主窗口计算动效目标值。文字始终显示真实百分比，只有进度条做平滑过渡，
    /// 因此动画不会改变用户对额度的判断。
    /// </summary>
    private void UpdateVisualTargets(bool animate)
    {
        QuotaWindow primary = FindDisplayWindow(18000, 0);
        QuotaWindow secondary = FindDisplayWindow(604800, 1, primary);
        targetPrimaryPercent = GetWindowPercent(primary);
        targetSecondaryPercent = GetWindowPercent(secondary);

        if (!animate || !settings.AnimationsEnabled)
        {
            animatedPrimaryPercent = targetPrimaryPercent;
            animatedSecondaryPercent = targetSecondaryPercent;
            visualAnimationActive = settings.AnimationsEnabled && Visible;
            UpdateVisualTimerInterval();
            return;
        }

        visualAnimationActive = true;
        visualPhase = 0f;
        UpdateVisualTimerInterval();
    }

    private static double GetWindowPercent(QuotaWindow window)
    {
        return window == null ? 0d : window.UsedPercent;
    }

    private static double StepVisualValue(double current, double target)
    {
        double delta = target - current;
        double distance = Math.Abs(delta);
        if (distance <= 0.05d)
        {
            return target;
        }

        double step = Math.Max(0.6d, distance * 0.22d);
        return current + Math.Sign(delta) * Math.Min(distance, step);
    }

    private void VisualTimerTick(object sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool shouldInvalidate = false;
        if (!settings.AnimationsEnabled)
        {
            if (animatedPrimaryPercent != targetPrimaryPercent || animatedSecondaryPercent != targetSecondaryPercent)
            {
                animatedPrimaryPercent = targetPrimaryPercent;
                animatedSecondaryPercent = targetSecondaryPercent;
                shouldInvalidate = true;
            }
            visualAnimationActive = false;
            refreshCelebrationActive = false;
            refreshCelebrationProgress = 0f;
        }
        else if (visualAnimationActive)
        {
            double nextPrimary = StepVisualValue(animatedPrimaryPercent, targetPrimaryPercent);
            double nextSecondary = StepVisualValue(animatedSecondaryPercent, targetSecondaryPercent);
            if (Math.Abs(nextPrimary - animatedPrimaryPercent) > 0.001d ||
                Math.Abs(nextSecondary - animatedSecondaryPercent) > 0.001d)
            {
                animatedPrimaryPercent = nextPrimary;
                animatedSecondaryPercent = nextSecondary;
            }

            visualPhase += 0.22f;
            if (visualPhase > (float)(Math.PI * 2d))
            {
                visualPhase -= (float)(Math.PI * 2d);
            }
            if (isRefreshing)
            {
                refreshRotation = (refreshRotation + 18) % 360;
            }

            if (refreshCelebrationActive)
            {
                refreshCelebrationProgress = Math.Min(1f, refreshCelebrationProgress + 0.06f);
                if (refreshCelebrationProgress >= 1f)
                {
                    refreshCelebrationActive = false;
                }
            }

            // 保持低频的待机呼吸，让状态栏在不刷新时也有明确的生命感；隐藏到托盘时会停止计时器。
            if (!Visible)
            {
                visualAnimationActive = false;
            }
            shouldInvalidate = true;
        }

        // 重置时间显示精确到分钟，15 秒检查一次即可避免跨分钟时文字停留旧值。
        if (now >= nextResetPaintAt)
        {
            nextResetPaintAt = now.AddSeconds(15d);
            shouldInvalidate = true;
        }

        UpdateVisualTimerInterval();
        if (shouldInvalidate)
        {
            Invalidate();
        }
    }

    private void UpdateVisualTimerInterval()
    {
        if (visualTimer == null)
        {
            return;
        }

        int interval = settings.AnimationsEnabled && visualAnimationActive ? 45 : 1000;
        if (visualTimer.Interval != interval)
        {
            visualTimer.Interval = interval;
        }
    }

    /// <summary>
    /// 统一处理 Provider 结果。只有在线成功结果会更新缓存和历史；失败时保留最近成功窗口，
    /// 同时把本次失败状态写入内存模型，便于状态栏、工具提示和诊断中心同时表达真实情况。
    /// </summary>
    private void ApplyUsageResult(UsageSnapshot result)
    {
        CancelRefreshCelebration();
        if (result == null)
        {
            ApplyQueryFailure(UsageStatus.UnknownError, "empty_result");
            return;
        }

        if (result.Status == UsageStatus.Live)
        {
            usageSnapshot = result;
            snapshot = result.ToQuotaSnapshot();
            UpdateVisualTargets(true);
            BeginRefreshCelebration();
            string cacheError;
            usageCache.TrySave(result, out cacheError);
            historyStore.Append(result);
            EvaluateNotifications(snapshot);
            NotifyUsageHubRefresh(result);
            return;
        }

        if (result.Status == UsageStatus.Cached)
        {
            usageSnapshot = result;
            snapshot = result.ToQuotaSnapshot();
            UpdateVisualTargets(true);
            NotifyUsageHubRefresh(result);
            return;
        }

        ApplyQueryFailure(result.Status, result.ErrorCode);
    }

    /// <summary>
    /// 把本次查询失败合并到最近成功快照。没有任何可用窗口时才显示纯错误状态，
    /// 避免网络短暂中断导致用户失去上一条仍然有效的额度信息。
    /// </summary>
    private void ApplyQueryFailure(UsageStatus status, string errorCode)
    {
        CancelRefreshCelebration();
        DateTimeOffset queriedAt = DateTimeOffset.Now;
        if (usageSnapshot != null && usageSnapshot.Windows != null && usageSnapshot.Windows.Count > 0)
        {
            usageSnapshot = usageSnapshot.WithFailure(status, errorCode, queriedAt);
            snapshot = usageSnapshot.ToQuotaSnapshot();
            UpdateVisualTargets(true);
            NotifyUsageHubRefresh(usageSnapshot);
            return;
        }

        usageSnapshot = UsageSnapshot.Failure("chatgpt-codex", status, errorCode, queriedAt);
        snapshot = usageSnapshot.ToQuotaSnapshot();
        UpdateVisualTargets(true);
        NotifyUsageHubRefresh(usageSnapshot);
    }

    /// <summary>
    /// 标记一次在线额度刷新成功。调用点位于统一结果入口，因此手动刷新、定时刷新和 Hub 刷新
    /// 都只会各自触发一次；缓存或失败结果不会伪装成成功庆祝。
    /// </summary>
    private void BeginRefreshCelebration()
    {
        if (!settings.AnimationsEnabled || !Visible)
        {
            CancelRefreshCelebration();
            visualAnimationActive = false;
            UpdateVisualTimerInterval();
            return;
        }

        refreshCelebrationProgress = 0f;
        refreshCelebrationActive = true;
        visualAnimationActive = true;
        UpdateVisualTimerInterval();
    }

    private void CancelRefreshCelebration()
    {
        refreshCelebrationActive = false;
        refreshCelebrationProgress = 0f;
    }

    private void NotifyUsageHubRefresh(UsageSnapshot result)
    {
        if (usageHubForm == null || usageHubForm.IsDisposed || result == null)
        {
            return;
        }

        try
        {
            // Usage Hub 是可选窗口，绘制同步失败不能影响主状态栏已经完成的额度刷新。
            usageHubForm.ApplyExternalRefresh(result, historyStore.Load());
        }
        catch (Exception)
        {
            // Hub 关闭或正在释放时忽略竞态，主刷新结果仍保留在状态栏和缓存中。
        }
    }

    private void EvaluateNotifications(QuotaSnapshot result)
    {
        IList<HistoryPoint> history = historyStore.Load();
        IList<UsageInsight> insights = UsageInsights.Build(usageSnapshot, history, DateTimeOffset.UtcNow);
        IList<UsageNotification> notifications = notificationEvaluator.EvaluateWithInsights(
            result,
            settings.NotificationThresholdPercent,
            settings.ResetNotificationsEnabled,
            settings.ForecastNotificationsEnabled,
            insights);
        if (!settings.NotificationsEnabled)
        {
            return;
        }

        if (notifications == null || notifications.Count == 0)
        {
            return;
        }

        if (notifications.Count == 1)
        {
            ShowNotificationSafely(notifications[0].Title, notifications[0].Message);
            return;
        }

        // 同一刷新可能同时跨过阈值、进入新周期并命中预测；合并成一个气泡，避免 NotifyIcon 后一条覆盖前一条。
        StringBuilder combined = new StringBuilder("本次刷新有 ");
        combined.Append(notifications.Count.ToString(CultureInfo.InvariantCulture));
        combined.AppendLine(" 项提醒");
        for (int index = 0; index < notifications.Count; index++)
        {
            if (index > 0)
            {
                combined.AppendLine();
            }
            combined.Append("· ");
            combined.Append(notifications[index].Message);
        }
        ShowNotificationSafely("额度状态更新", combined.ToString());
    }

    /// <summary>
    /// 通知图标属于可选的桌面能力。系统策略、托盘重建或退出竞态导致通知失败时，不能把已经成功取得的额度标记为刷新失败。
    /// </summary>
    private void ShowNotificationSafely(string title, string message)
    {
        try
        {
            trayController.ShowNotification(title, message);
        }
        catch (Exception)
        {
            try
            {
                toolTip.SetToolTip(this, "通知暂不可用，状态栏仍可继续使用");
            }
            catch (Exception)
            {
                // Tooltip 也可能在窗口销毁竞态中不可用，此时静默保留主刷新结果。
            }
        }
    }

    /// <summary>
    /// 汇总可安全提交到 Issue 的运行信息。这里明确排除令牌、账户标识、代理地址和完整响应。
    /// </summary>
    private string BuildDiagnosticReport()
    {
        return BuildDiagnosticSnapshot().Report;
    }

    private DiagnosticSnapshot BuildDiagnosticSnapshot()
    {
        string credentialDiagnostic = usageProvider.GetCredentialDiagnostic();
        string networkDiagnostic = usageProvider.GetNetworkDiagnostic();
        IList<HistoryPoint> history = historyStore.Load();
        IList<UsageInsight> insights = UsageInsights.Build(usageSnapshot, history, DateTimeOffset.UtcNow);
        bool forecastAvailable = false;
        foreach (UsageInsight insight in insights)
        {
            if (insight != null && insight.ProjectedExhaustionAt.HasValue)
            {
                forecastAvailable = true;
                break;
            }
        }
        string report = diagnosticsService.BuildExtended(
            snapshot,
            credentialDiagnostic,
            networkDiagnostic,
            autoStartEnabled,
            !string.IsNullOrWhiteSpace(autoStartError),
            settings,
            history.Count,
            forecastAvailable,
            settings.GlobalHotkeyEnabled,
            globalHotkey.IsRegistered,
            settings.ResetNotificationsEnabled,
            settings.ForecastNotificationsEnabled,
            snapshot == null ? (DateTimeOffset?)null : snapshot.LastLiveAt,
            DateTimeOffset.UtcNow);
        IList<DiagnosticCheck> checks = diagnosticsService.BuildChecksExtended(
            snapshot,
            credentialDiagnostic,
            networkDiagnostic,
            autoStartEnabled,
            !string.IsNullOrWhiteSpace(autoStartError),
            settings,
            history.Count,
            forecastAvailable,
            settings.GlobalHotkeyEnabled,
            globalHotkey.IsRegistered,
            settings.ResetNotificationsEnabled,
            settings.ForecastNotificationsEnabled,
            snapshot == null ? (DateTimeOffset?)null : snapshot.LastLiveAt,
            DateTimeOffset.UtcNow);
        return new DiagnosticSnapshot(report, checks);
    }

    private async void RunDiagnostics(object sender, EventArgs e)
    {
        await RefreshQuotaSafelyAsync();
        if (!cancellation.IsCancellationRequested)
        {
            ShowDiagnosticReport();
        }
    }

    private void ShowDiagnosticReport()
    {
        DiagnosticSnapshot initialSnapshot = BuildDiagnosticSnapshot();
        using (DiagnosticsForm form = new DiagnosticsForm(
            initialSnapshot.Report,
            initialSnapshot.Checks,
            RefreshDiagnosticsForFormAsync,
            settings.Theme,
            OpenDataFolder,
            ExportHistoryCsv))
        {
            form.ShowDialog(this);
        }
    }

    private async Task<DiagnosticSnapshot> RefreshDiagnosticsForFormAsync()
    {
        await RefreshQuotaSafelyAsync();
        return BuildDiagnosticSnapshot();
    }

    private void CopyDiagnosticReport()
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticReport());
            MessageBox.Show(this, "诊断信息已复制，可安全粘贴到 Issue（不含凭据）。", "复制成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法访问剪贴板，请使用“诊断中心”查看信息。", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 将本地趋势导出为脱敏 CSV，并在资源管理器中定位文件。导出只包含窗口、百分比和时间，不会读取 OAuth 文件。
    /// </summary>
    private void ExportHistoryCsv()
    {
        string exportPath = historyStore.ExportCsv();
        if (string.IsNullOrWhiteSpace(exportPath))
        {
            MessageBox.Show(this, "暂无可导出的趋势，或本地文件暂时不可写。", "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + exportPath + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "趋势已导出：\r\n" + exportPath, "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenDataFolder()
    {
        try
        {
            string directory = LocalStoragePaths.RootDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法打开应用数据目录。", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearHistoryData()
    {
        DialogResult choice = MessageBox.Show(
            this,
            "将删除本项目保存的趋势历史和导出文件，不会影响最近成功缓存或 Codex 登录。是否继续？",
            "清除趋势历史",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        historyStore.Clear();
        RefreshLocalPresentation();
    }

    private void ClearCacheData()
    {
        DialogResult choice = MessageBox.Show(
            this,
            "将删除最近成功额度缓存，不会删除趋势历史、导出文件或 Codex 登录。是否继续？",
            "清除最近成功缓存",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        usageCache.Clear();
        RefreshLocalPresentation();
    }

    private void RefreshLocalPresentation()
    {
        toolTip.SetToolTip(this, BuildTooltipText());
        trayController.SetStatus(BuildTrayStatus());
        Invalidate();
    }

    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法打开项目主页。", "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 查询公开 Release 并让用户决定是否打开下载页。应用不会在后台替换正在运行的可执行文件。
    /// </summary>
    private Task CheckForUpdatesAsync(ToolStripMenuItem menuItem)
    {
        if (cancellation.IsCancellationRequested)
        {
            return Task.FromResult(0);
        }

        if (updateCheckInFlight != null && !updateCheckInFlight.IsCompleted)
        {
            return updateCheckInFlight;
        }

        updateCheckInFlight = CheckForUpdatesCoreAsync(menuItem);
        return updateCheckInFlight;
    }

    private async Task CheckForUpdatesCoreAsync(ToolStripMenuItem menuItem)
    {
        if (menuItem != null)
        {
            menuItem.Enabled = false;
            menuItem.Text = "检查更新中…";
        }

        try
        {
            UpdateCheckResult result = await updateService.CheckLatestAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }
            if (!result.IsSuccess)
            {
                MessageBox.Show(this, "暂时无法检查更新（" + result.ErrorCode + "）。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!result.IsUpdateAvailable)
            {
                MessageBox.Show(this, "当前已是最新版本 v" + UpdateService.CurrentVersion + "。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string message = "发现新版本 v" + result.LatestVersion + "。\r\n";
            if (!string.IsNullOrWhiteSpace(result.AssetName))
            {
                message += "可下载资产：" + result.AssetName + "\r\n";
            }
            if (!string.IsNullOrWhiteSpace(result.Sha256))
            {
                message += "SHA-256：" + result.Sha256 + "\r\n";
                message += "下载后可运行：Get-FileHash .\\SubscriptionStatus.exe -Algorithm SHA256\r\n";
            }
            message += "是否打开 GitHub Release 页面？";
            DialogResult choice = MessageBox.Show(this, message, "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (choice == DialogResult.Yes && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
            {
                Process.Start(new ProcessStartInfo(result.ReleaseUrl) { UseShellExecute = true });
            }
        }
        catch (Exception)
        {
            MessageBox.Show(this, "检查更新失败，请稍后重试。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            if (menuItem != null && !IsDisposed)
            {
                menuItem.Enabled = true;
                menuItem.Text = "检查更新";
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (LinearGradientBrush background = new LinearGradientBrush(
            ClientRectangle,
            themePalette.BackgroundTop,
            themePalette.BackgroundBottom,
            8f))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        int topLineAlpha = 190;
        if (settings.AnimationsEnabled && visualAnimationActive)
        {
            double breath = (Math.Sin(visualPhase * 0.72d) + 1d) / 2d;
            topLineAlpha = 166 + (int)Math.Round(breath * 54d);
        }
        using (Pen topLine = new Pen(UiTheme.WithAlpha(themePalette.PrimaryAccent, topLineAlpha), 2f))
        {
            g.DrawLine(topLine, 10, 1, WindowWidth - 10, 1);
        }

        DrawRefreshCelebration(g);
        DrawAmbientPulse(g);

        Color borderColor = visualAnimationActive && settings.AnimationsEnabled
            ? BlendColors(themePalette.Border, themePalette.PrimaryAccent, 0.35f)
            : themePalette.Border;
        using (Pen border = new Pen(borderColor, 1f))
        using (GraphicsPath borderPath = RoundedRectangle(new Rectangle(0, 0, WindowWidth - 1, WindowHeight - 1), 12))
        {
            g.DrawPath(border, borderPath);
        }

        DrawBrand(g);
        DrawDivider(g, 78);
        QuotaWindow primaryWindow = FindDisplayWindow(18000, 0);
        QuotaWindow secondaryWindow = FindDisplayWindow(604800, 1, primaryWindow);
        DrawWindow(g, new Rectangle(88, 0, 78, WindowHeight), primaryWindow, CompactWindowName(primaryWindow, "1"), themePalette.PrimaryAccent, animatedPrimaryPercent);
        DrawDivider(g, 174);
        DrawWindow(g, new Rectangle(184, 0, 78, WindowHeight), secondaryWindow, CompactWindowName(secondaryWindow, "2"), themePalette.SecondaryAccent, animatedSecondaryPercent);
        DrawDivider(g, 270);
        DrawStatus(g);
        DrawActionButtons(g);
    }

    private void DrawBrand(Graphics g)
    {
        Color statusColor = GetOverallStatusColor();
        if (settings.AnimationsEnabled && visualAnimationActive)
        {
            double wave = (Math.Sin(visualPhase * 0.9d) + 1d) / 2d;
            int haloAlpha = 18 + (int)Math.Round(wave * 28d);
            int haloSize = 11 + (int)Math.Round(wave * 5d);
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(haloAlpha, statusColor)))
            {
                g.FillEllipse(halo, 14 - haloSize / 2, 21 - haloSize / 2, haloSize, haloSize);
            }
        }
        using (SolidBrush dot = new SolidBrush(statusColor))
        {
            g.FillEllipse(dot, 10, 17, 8, 8);
        }

        DrawAlignedText(
            g,
            CompactPlanName(),
            UiTheme.UiFontFamily,
            9.2f,
            FontStyle.Bold,
            themePalette.PrimaryText,
            new Rectangle(23, 5, 51, 20),
            StringAlignment.Near,
            StringAlignment.Center);
    }

    /// <summary>
    /// 绘制一次成功刷新后的固定中心律动。动画只改变同心脉冲的半径和透明度，
    /// 不在状态栏上横向扫过，避免干扰数字阅读。
    /// </summary>
    private void DrawRefreshCelebration(Graphics g)
    {
        if (!settings.AnimationsEnabled || !refreshCelebrationActive)
        {
            return;
        }

        float progress = Math.Max(0f, Math.Min(1f, refreshCelebrationProgress));
        float fade = 1f - progress;
        float centerX = 282f;
        float centerY = 20f;
        float pulse = (float)Math.Sin(progress * Math.PI);
        for (int ringIndex = 0; ringIndex < 2; ringIndex++)
        {
            float ringProgress = Math.Min(1f, progress + ringIndex * 0.16f);
            float radius = 7f + ringProgress * 12f;
            int ringAlpha = Math.Max(0, (int)Math.Round((142f - ringIndex * 35f) * (1f - ringProgress)));
            using (Pen ring = new Pen(UiTheme.WithAlpha(themePalette.SecondaryAccent, ringAlpha), 1.2f + pulse * 0.5f))
            {
                g.DrawEllipse(ring, centerX - radius, centerY - radius, radius * 2f, radius * 2f);
            }
        }

        int rayAlpha = Math.Max(0, (int)Math.Round(120f * fade));
        using (Pen rays = new Pen(UiTheme.WithAlpha(themePalette.PrimaryAccent, rayAlpha), 1.1f))
        {
            rays.StartCap = LineCap.Round;
            rays.EndCap = LineCap.Round;
            for (int index = 0; index < 8; index++)
            {
                double angle = index * Math.PI / 4d;
                float inner = 9f + pulse * 2f;
                float outer = inner + 2f + 2f * fade;
                float x1 = centerX + (float)Math.Cos(angle) * inner;
                float y1 = centerY + (float)Math.Sin(angle) * inner;
                float x2 = centerX + (float)Math.Cos(angle) * outer;
                float y2 = centerY + (float)Math.Sin(angle) * outer;
                g.DrawLine(rays, x1, y1, x2, y2);
            }
        }
    }

    private string CompactPlanName()
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.PlanName))
        {
            return "GPT";
        }

        switch (snapshot.PlanName)
        {
            case "GPT Free":
                return "FREE";
            case "GPT Go":
                return "GO";
            case "GPT Plus":
                return "PLUS";
            case "GPT Pro":
                return "PRO";
            case "GPT Team":
                return "TEAM";
            case "GPT Business":
                return "BIZ";
            case "GPT Enterprise":
                return "ENT";
            case "GPT Edu":
                return "EDU";
            default:
                return "GPT";
        }
    }

    private void DrawWindow(Graphics g, Rectangle bounds, QuotaWindow window, string compactName, Color accent, double visualPercent)
    {
        double usedPercent = window == null ? 0d : window.UsedPercent;
        string percentage = window == null ? "--" : usedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        int trackWidth = Math.Max(1, bounds.Width - 6);
        double safeVisualPercent = Math.Max(0d, Math.Min(100d, visualPercent));
        int fillWidth = (int)Math.Round(trackWidth * safeVisualPercent / 100d);
        fillWidth = Math.Max(0, Math.Min(trackWidth, fillWidth));
        Color fillColor = GetUsageColor(usedPercent, accent);

        Rectangle header = new Rectangle(bounds.Left + 3, 5, bounds.Width - 6, 17);
        DrawAlignedText(
            g,
            compactName,
            UiTheme.UiFontFamily,
            7.8f,
            FontStyle.Bold,
            themePalette.SecondaryText,
            header,
            StringAlignment.Near,
            StringAlignment.Center);
        DrawAlignedText(
            g,
            percentage,
            UiTheme.UiFontFamily,
            10.2f,
            FontStyle.Bold,
            themePalette.PrimaryText,
            header,
            StringAlignment.Far,
            StringAlignment.Center);

        Rectangle track = new Rectangle(bounds.Left + 1, 28, trackWidth, 5);
        using (SolidBrush trackBrush = new SolidBrush(themePalette.Track))
        using (GraphicsPath trackPath = RoundedRectangle(track, 2))
        {
            g.FillPath(trackBrush, trackPath);
        }
        if (fillWidth > 0)
        {
            Rectangle fill = new Rectangle(track.Left, track.Top, fillWidth, track.Height);
            using (SolidBrush glowBrush = new SolidBrush(Color.FromArgb(40, fillColor)))
            using (GraphicsPath glowPath = RoundedRectangle(new Rectangle(fill.Left, fill.Top - 1, fill.Width, fill.Height + 2), 2))
            using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                fill,
                BlendColors(fillColor, Color.White, 0.28f),
                fillColor,
                90f))
            using (GraphicsPath fillPath = RoundedRectangle(fill, 2))
            {
                g.FillPath(glowBrush, glowPath);
                g.FillPath(fillBrush, fillPath);
                if (settings.AnimationsEnabled && visualAnimationActive && fill.Width > 7)
                {
                    double wave = (Math.Sin(visualPhase * 0.85d) + 1d) / 2d;
                    int highlightAlpha = 45 + (int)Math.Round(wave * 48d);
                    using (SolidBrush shine = new SolidBrush(Color.FromArgb(highlightAlpha, Color.White)))
                    {
                        g.FillRectangle(shine, fill.Left + 1, fill.Top, Math.Max(1, fill.Width - 2), 1);
                    }
                }
            }
        }

        // 底部直接显示日期和下一次重置时间；扩大额度列宽度后避免日期与分隔线重叠。
        string reset = window == null ? "--" : FormatVisibleReset(window.ResetAt);
        DrawAlignedText(
            g,
            reset,
            UiTheme.UiFontFamily,
            8f,
            FontStyle.Bold,
            themePalette.SecondaryText,
            new Rectangle(bounds.Left + 1, 36, bounds.Width - 2, 16),
            StringAlignment.Center,
            StringAlignment.Center);
    }

    private void DrawStatus(Graphics g)
    {
        Color statusColor = GetOverallStatusColor();
        if (settings.AnimationsEnabled && visualAnimationActive)
        {
            double wave = (Math.Sin(visualPhase * 0.85d + 1.2d) + 1d) / 2d;
            float radius = 5.5f + (float)(wave * 2.5d);
            using (Pen pulse = new Pen(Color.FromArgb(38 + (int)Math.Round(wave * 42d), statusColor), 1f))
            {
                g.DrawEllipse(pulse, 282f - radius, 20f - radius, radius * 2f, radius * 2f);
            }
        }
        using (SolidBrush dot = new SolidBrush(statusColor))
        {
            g.FillEllipse(dot, 279, 17, 6, 6);
        }
    }

    /// <summary>
    /// 为常驻状态栏提供几乎静止的整体呼吸边缘。固定位置的透明度变化不会打断窄条信息的阅读节奏。
    /// </summary>
    private void DrawAmbientPulse(Graphics g)
    {
        if (!settings.AnimationsEnabled || !visualAnimationActive)
        {
            return;
        }

        double wave = (Math.Sin(visualPhase * 0.72d) + 1d) / 2d;
        int alpha = 8 + (int)Math.Round(wave * 15d);
        using (Pen pulse = new Pen(UiTheme.WithAlpha(themePalette.SecondaryAccent, alpha), 1f))
        using (GraphicsPath path = RoundedRectangle(new Rectangle(4, 4, WindowWidth - 9, WindowHeight - 9), 8))
        {
            g.DrawPath(pulse, path);
        }
    }

    private void DrawActionButtons(Graphics g)
    {
        bool refreshHover = refreshArea.Contains(PointToClient(Cursor.Position));
        bool closeHover = closeArea.Contains(PointToClient(Cursor.Position));
        bool expandHover = expandArea.Contains(PointToClient(Cursor.Position));
        DrawButtonSurface(g, expandArea, expandHover);
        DrawButtonSurface(g, refreshArea, refreshHover);
        DrawButtonSurface(g, closeArea, closeHover);

        using (Pen expandPen = new Pen(themePalette.ButtonIcon, 1.35f))
        {
            expandPen.StartCap = LineCap.Round;
            expandPen.EndCap = LineCap.Round;
            int left = expandArea.Left + 5;
            int top = expandArea.Top + 5;
            int right = expandArea.Right - 5;
            int bottom = expandArea.Bottom - 5;
            g.DrawLine(expandPen, left, top + 4, left, top);
            g.DrawLine(expandPen, left, top, left + 4, top);
            g.DrawLine(expandPen, right, bottom - 4, right, bottom);
            g.DrawLine(expandPen, right - 4, bottom, right, bottom);
        }

        using (Pen refreshPen = new Pen(themePalette.ButtonIcon, 1.4f))
        {
            refreshPen.StartCap = LineCap.Round;
            refreshPen.EndCap = LineCap.Round;
            int rotation = isRefreshing && settings.AnimationsEnabled ? refreshRotation : 0;
            g.DrawArc(refreshPen, new Rectangle(refreshArea.Left + 4, refreshArea.Top + 4, 12, 12), 35 + rotation, 285);
            Point[] arrow =
            {
                new Point(refreshArea.Left + 16, refreshArea.Top + 6),
                new Point(refreshArea.Left + 16, refreshArea.Top + 10),
                new Point(refreshArea.Left + 12, refreshArea.Top + 9)
            };
            g.DrawLines(refreshPen, arrow);
        }

        using (Pen closePen = new Pen(themePalette.ButtonIcon, 1.5f))
        {
            closePen.StartCap = LineCap.Round;
            closePen.EndCap = LineCap.Round;
            g.DrawLine(closePen, closeArea.Left + 5, closeArea.Top + 5, closeArea.Right - 5, closeArea.Bottom - 5);
            g.DrawLine(closePen, closeArea.Right - 5, closeArea.Top + 5, closeArea.Left + 5, closeArea.Bottom - 5);
        }
    }

    private void DrawButtonSurface(Graphics g, Rectangle area, bool hover)
    {
        using (SolidBrush brush = new SolidBrush(hover ? themePalette.ButtonHover : Color.Transparent))
        using (GraphicsPath path = RoundedRectangle(area, 7))
        {
            g.FillPath(brush, path);
            if (hover)
            {
                using (Pen hoverBorder = new Pen(Color.FromArgb(120, themePalette.SecondaryAccent), 1f))
                {
                    g.DrawPath(hoverBorder, path);
                }
            }
        }
    }

    private Color GetOverallStatusColor()
    {
        if (snapshot == null || !snapshot.Success)
        {
            return snapshot != null && snapshot.StatusText == "读取中"
                ? themePalette.SecondaryAccent
                : themePalette.Error;
        }
        return snapshot.IsStale ? themePalette.Warning : themePalette.Success;
    }

    private Color GetUsageColor(double usedPercent, Color accent)
    {
        if (usedPercent >= 95d)
        {
            return themePalette.Error;
        }
        if (usedPercent >= 80d)
        {
            return themePalette.Warning;
        }
        return accent;
    }

    private static Color BlendColors(Color first, Color second, float secondWeight)
    {
        float weight = Math.Max(0f, Math.Min(1f, secondWeight));
        float firstWeight = 1f - weight;
        return Color.FromArgb(
            (int)Math.Round(first.A * firstWeight + second.A * weight),
            (int)Math.Round(first.R * firstWeight + second.R * weight),
            (int)Math.Round(first.G * firstWeight + second.G * weight),
            (int)Math.Round(first.B * firstWeight + second.B * weight));
    }

    private QuotaWindow FindDisplayWindow(int seconds, int fallbackIndex, QuotaWindow excluded = null)
    {
        if (snapshot == null || snapshot.Windows == null)
        {
            return null;
        }
        foreach (QuotaWindow window in snapshot.Windows)
        {
            if (window != null && window.LimitWindowSeconds == seconds && !object.ReferenceEquals(window, excluded))
            {
                return window;
            }
        }
        if (fallbackIndex >= 0 && fallbackIndex < snapshot.Windows.Count)
        {
            QuotaWindow fallback = snapshot.Windows[fallbackIndex];
            if (fallback != null && !object.ReferenceEquals(fallback, excluded))
            {
                return fallback;
            }
        }
        return null;
    }

    private static string CompactWindowName(QuotaWindow window, string fallback)
    {
        if (window == null)
        {
            return fallback;
        }
        if (window.LimitWindowSeconds == 18000)
        {
            return "5h";
        }
        if (window.LimitWindowSeconds == 604800)
        {
            return "7d";
        }
        if (window.LimitWindowSeconds >= 86400)
        {
            return (window.LimitWindowSeconds / 86400).ToString(CultureInfo.InvariantCulture) + "d";
        }
        if (window.LimitWindowSeconds >= 3600)
        {
            return (window.LimitWindowSeconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        }
        if (window.LimitWindowSeconds >= 60)
        {
            return (window.LimitWindowSeconds / 60).ToString(CultureInfo.InvariantCulture) + "m";
        }
        return "win";
    }

    private string BuildTooltipText()
    {
        string text = snapshot.PlanName + "\r\n" + snapshot.AccountLabel;
        foreach (QuotaWindow window in snapshot.Windows)
        {
            text += "\r\n" + window.Name + ": " + window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%，重置 " + FormatCompactReset(window.ResetAt);
        }
        if (snapshot.IsStale && snapshot.LastLiveAt.HasValue)
        {
            text += "\r\n缓存时间 " + snapshot.LastLiveAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        if ((!snapshot.Success || snapshot.IsStale) && !string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            text += "\r\n" + snapshot.ErrorText;
        }
        text += "\r\n点击展开 Usage Hub；右键：刷新周期、数据导出、背景样式、开机自启和诊断";
        if (settings.ClickThroughEnabled)
        {
            text += "\r\n展示模式：鼠标点击已穿透，请从托盘菜单取消";
        }
        if (settings.GlobalHotkeyEnabled)
        {
            text += globalHotkeyRegistrationFailed
                ? "\r\n快捷键 Ctrl+Alt+U：注册冲突，请查看诊断"
                : "\r\n快捷键 Ctrl+Alt+U：唤起 Usage Hub";
        }
        return text;
    }

    private static string FormatCompactReset(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue)
        {
            return "--";
        }

        TimeSpan remaining = resetAt.Value - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0)
        {
            return "now";
        }
        if (remaining.TotalDays >= 1)
        {
            return resetAt.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
        }
        return resetAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatVisibleReset(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue)
        {
            return "--";
        }

        if ((resetAt.Value - DateTimeOffset.UtcNow).TotalSeconds <= 0)
        {
            return "now";
        }
        return resetAt.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void DrawDivider(Graphics g, int x)
    {
        using (Pen divider = new Pen(themePalette.Divider))
        {
            g.DrawLine(divider, x, 7, x, WindowHeight - 7);
        }
    }

    private static void DrawAlignedText(
        Graphics g,
        string text,
        string family,
        float size,
        FontStyle style,
        Color color,
        Rectangle bounds,
        StringAlignment horizontal,
        StringAlignment vertical)
    {
        using (Font font = new Font(family, size, style, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = horizontal;
            format.LineAlignment = vertical;
            format.FormatFlags = StringFormatFlags.NoWrap;
            format.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(text ?? string.Empty, font, brush, bounds, format);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (settings.ClickThroughEnabled)
        {
            return;
        }
        if (e.Button == MouseButtons.Left && !closeArea.Contains(e.Location) && !refreshArea.Contains(e.Location) && !expandArea.Contains(e.Location))
        {
            mouseDownLocation = e.Location;
            draggingBar = false;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }
    }

    protected override async void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (settings.ClickThroughEnabled)
        {
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (closeArea.Contains(e.Location))
        {
            HideToTray();
            return;
        }
        if (expandArea.Contains(e.Location))
        {
            ShowUsageHub();
            return;
        }
        if (refreshArea.Contains(e.Location))
        {
            await RefreshQuotaSafelyAsync();
            return;
        }
        bool clickedAfterDrag = draggingBar ||
            Math.Abs(e.Location.X - mouseDownLocation.X) > 6 ||
            Math.Abs(e.Location.Y - mouseDownLocation.Y) > 6;
        draggingBar = false;
        if (!clickedAfterDrag)
        {
            ShowUsageHub();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (settings.ClickThroughEnabled)
        {
            return;
        }
        if (e.Button == MouseButtons.Left && !closeArea.Contains(e.Location) &&
            !refreshArea.Contains(e.Location) && !expandArea.Contains(e.Location))
        {
            ShowDetails();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (settings.ClickThroughEnabled)
        {
            Cursor = Cursors.Default;
            return;
        }
        if (e.Button == MouseButtons.Left && !draggingBar &&
            (Math.Abs(e.Location.X - mouseDownLocation.X) > 6 || Math.Abs(e.Location.Y - mouseDownLocation.Y) > 6))
        {
            draggingBar = true;
            userMoved = true;
        }
        Cursor = closeArea.Contains(e.Location) || refreshArea.Contains(e.Location) || expandArea.Contains(e.Location)
            ? Cursors.Hand
            : Cursors.SizeAll;
        Invalidate(new Rectangle(WindowWidth - 92, 7, 84, 38));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        Invalidate(new Rectangle(WindowWidth - 92, 7, 84, 38));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class Program
{
    private const int SwRestore = 9;
    private const int ExistingWindowWaitMilliseconds = 2000;
    private const int ExistingWindowPollMilliseconds = 50;

    private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [STAThread]
    private static void Main()
    {
        try
        {
            // 开机自启和手动启动可能同时发生，使用本地互斥体确保屏幕上只有一个状态栏。
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\ChatGPTCodexUsageStatusBar", out createdNew))
            {
                if (!createdNew)
                {
                    // 重复双击不再静默退出，直接唤起已经运行的隐藏或最小化窗口。
                    FocusExistingInstance();
                    return;
                }

                DpiSupport.Enable();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += HandleUiThreadException;
                Application.Run(new StatusWindow());
            }
        }
        catch (Exception)
        {
            // winexe 默认没有控制台，启动构造失败时必须给出可操作的反馈，而不是让双击看起来毫无反应。
            ShowStartupError();
        }
    }

    /// <summary>
    /// 将第二次启动转换为“显示并聚焦”，兼容用户把主窗口隐藏到托盘后的再次双击。
    /// </summary>
    private static void FocusExistingInstance()
    {
        try
        {
            // 互斥锁先于 WinForms 句柄创建，第二次双击可能正好落在首实例构造窗口期；
            // 短暂轮询避免这类竞态再次表现为“没有反应”。
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(ExistingWindowWaitMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                IntPtr handle = FindStatusWindow();
                if (handle != IntPtr.Zero)
                {
                    ShowWindow(handle, SwRestore);
                    if (SetForegroundWindow(handle))
                    {
                        return;
                    }
                }
                Thread.Sleep(ExistingWindowPollMilliseconds);
            }
        }
        catch (Exception)
        {
            // 聚焦只是增强反馈，失败时不能影响已经运行的主实例。
        }
    }

    /// <summary>
    /// 通过枚举当前桌面的顶层窗口查找状态栏，规避部分 Windows 桌面环境下
    /// FindWindow 对无边框 WinForms 窗口标题匹配不稳定的问题。
    /// </summary>
    private static IntPtr FindStatusWindow()
    {
        IntPtr matchedHandle = IntPtr.Zero;
        EnumWindowsCallback callback = delegate(IntPtr hWnd, IntPtr lParam)
        {
            StringBuilder title = new StringBuilder(128);
            GetWindowText(hWnd, title, title.Capacity);
            if (string.Equals(title.ToString(), "ChatGPT quota", StringComparison.Ordinal))
            {
                matchedHandle = hWnd;
                return false;
            }

            return true;
        };

        EnumWindows(callback, IntPtr.Zero);
        return matchedHandle;
    }

    /// <summary>
    /// UI 线程未预期异常的统一提示。只展示修复方向，不泄露 OAuth、账户或本机路径。
    /// </summary>
    private static void HandleUiThreadException(object sender, ThreadExceptionEventArgs args)
    {
        ShowStartupError();
    }

    private static void ShowStartupError()
    {
        try
        {
            MessageBox.Show(
                "状态栏启动失败。请确认完整保留 dist 文件夹，并重新运行 start-statusbar.cmd。\r\n\r\n如果已有托盘图标，可从“诊断中心”复制脱敏信息；否则请提交启动失败现象。",
                "ChatGPT/Codex 状态栏",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // 桌面会话不可用时无法显示对话框，保持进程安全退出即可。
        }
    }
}
