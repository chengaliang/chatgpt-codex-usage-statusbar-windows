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
    public IList<QuotaWindow> Windows { get; private set; }

    private QuotaSnapshot()
    {
        PlanName = "ChatGPT";
        AccountLabel = "账户未识别";
        StatusText = "读取中";
        ErrorText = string.Empty;
        Windows = new List<QuotaWindow>();
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
        result.Windows = windows;
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

        return "OAuth：ChatGPT OAuth 配置可读取\r\n计划：" + SanitizeDiagnosticValue(credentials.PlanName, "ChatGPT");
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

    private static string SanitizeDiagnosticValue(string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (sanitized.Length > 32)
        {
            sanitized = sanitized.Substring(0, 32);
        }
        return sanitized;
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
        string idToken = GetString(tokens, "id_token");
        if (!string.IsNullOrWhiteSpace(idToken))
        {
            string[] segments = idToken.Split('.');
            if (segments.Length >= 2)
            {
                try
                {
                    string payload = DecodeBase64Url(segments[1]);
                    Dictionary<string, object> claims = serializer.DeserializeObject(payload) as Dictionary<string, object>;
                    string planType = GetString(claims, "https://api.openai.com/auth.chatgpt_plan_type");
                    if (string.Equals(planType, "plus", StringComparison.OrdinalIgnoreCase))
                    {
                        return "GPT Plus";
                    }
                    if (!string.IsNullOrWhiteSpace(planType))
                    {
                        return "GPT " + planType.ToUpperInvariant();
                    }
                }
                catch (Exception)
                {
                    // 计划声明只用于标题，解析失败时使用安全的通用名称。
                }
            }
        }

        return "ChatGPT";
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
    private const int WindowWidth = 320;
    private const int WindowHeight = 40;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "ChatGPTCodexUsageStatusBar";
    private const string StartupConfiguredValueName = "ChatGPTCodexUsageStatusBarConfigured";
    private const string ProjectUrl = "https://github.com/chengaliang/chatgpt-codex-usage-statusbar-windows";
    private readonly Rectangle closeArea = new Rectangle(WindowWidth - 24, 11, 18, 18);
    private readonly Rectangle refreshArea = new Rectangle(WindowWidth - 47, 11, 18, 18);
    private readonly OfficialQuotaService quotaService;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private readonly CancellationTokenSource cancellation;
    private readonly ToolTip toolTip;
    private readonly ContextMenuStrip contextMenu;
    private ToolStripMenuItem autoStartMenuItem;
    private QuotaSnapshot snapshot;
    private bool isRefreshing;
    private bool userMoved;
    private bool autoStartEnabled;
    private string autoStartError;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out NativeRect rectangle, uint update);

    private const uint SpiGetWorkArea = 0x0030;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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

        quotaService = new OfficialQuotaService();
        cancellation = new CancellationTokenSource();
        snapshot = QuotaSnapshot.Loading();
        autoStartError = string.Empty;
        InitializeAutoStart();
        contextMenu = CreateContextMenu();
        ContextMenuStrip = contextMenu;
        toolTip = new ToolTip();
        toolTip.AutoPopDelay = 8000;
        toolTip.InitialDelay = 350;
        toolTip.ReshowDelay = 100;
        toolTip.SetToolTip(this, BuildTooltipText());

        refreshTimer = new System.Windows.Forms.Timer();
        refreshTimer.Interval = 5 * 60 * 1000;
        refreshTimer.Tick += async delegate(object sender, EventArgs args) { await RefreshQuotaSafelyAsync(); };
        refreshTimer.Start();
    }

    /// <summary>
    /// 初始化当前用户的启动项。第一次运行默认启用，之后尊重用户在右键菜单中的选择。
    /// 使用 HKCU 不需要管理员权限，也不会影响其他 Windows 用户的登录行为。
    /// </summary>
    private void InitializeAutoStart()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            {
                if (key == null)
                {
                    autoStartError = "无法访问当前用户启动项";
                    return;
                }

                object configured = key.GetValue(StartupConfiguredValueName);
                if (configured == null)
                {
                    key.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                    key.SetValue(StartupConfiguredValueName, "1", RegistryValueKind.String);
                    autoStartEnabled = true;
                    return;
                }

                string command = key.GetValue(StartupValueName) as string;
                autoStartEnabled = !string.IsNullOrWhiteSpace(command);
                if (autoStartEnabled && !string.Equals(command, GetStartupCommand(), StringComparison.OrdinalIgnoreCase))
                {
                    // 程序被移动后修复旧路径，避免开机启动指向不存在的文件。
                    key.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                }
            }
        }
        catch (Exception)
        {
            autoStartEnabled = false;
            autoStartError = "无法写入当前用户启动项";
        }
    }

    private static string GetStartupCommand()
    {
        // Application.ExecutablePath 来自当前进程，双引号保证安装路径含空格时仍能正确启动。
        return "\"" + Application.ExecutablePath + "\"";
    }

    /// <summary>
    /// 更新当前用户的启动项并保留配置标记，使用户关闭自启后不会在下次启动被强制打开。
    /// </summary>
    private bool TrySetAutoStart(bool enabled)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            {
                if (key == null)
                {
                    autoStartError = "无法访问当前用户启动项";
                    return false;
                }

                if (enabled)
                {
                    key.SetValue(StartupValueName, GetStartupCommand(), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(StartupValueName, false);
                }

                key.SetValue(StartupConfiguredValueName, "1", RegistryValueKind.String);
                autoStartEnabled = enabled;
                autoStartError = string.Empty;
                return true;
            }
        }
        catch (Exception)
        {
            autoStartError = "无法更新当前用户启动项";
            return false;
        }
    }

    /// <summary>
    /// 构造右键菜单。状态栏保持极简，设置、诊断和项目入口集中在这里。
    /// </summary>
    private ContextMenuStrip CreateContextMenu()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;

        ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
        refreshItem.Click += async delegate(object sender, EventArgs args) { await RefreshQuotaSafelyAsync(); };

        autoStartMenuItem = new ToolStripMenuItem("开机自启");
        autoStartMenuItem.CheckOnClick = true;
        autoStartMenuItem.Checked = autoStartEnabled;
        autoStartMenuItem.Click += delegate(object sender, EventArgs args)
        {
            bool requested = autoStartMenuItem.Checked;
            if (!TrySetAutoStart(requested))
            {
                autoStartMenuItem.Checked = autoStartEnabled;
                MessageBox.Show(this, autoStartError, "开机自启", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        ToolStripMenuItem diagnosticsItem = new ToolStripMenuItem("运行诊断");
        diagnosticsItem.Click += RunDiagnostics;

        ToolStripMenuItem copyDiagnosticsItem = new ToolStripMenuItem("复制诊断信息");
        copyDiagnosticsItem.Click += delegate(object sender, EventArgs args) { CopyDiagnosticReport(); };

        ToolStripMenuItem projectItem = new ToolStripMenuItem("打开项目主页");
        projectItem.Click += delegate(object sender, EventArgs args) { OpenProjectPage(); };

        ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += delegate(object sender, EventArgs args) { Close(); };

        menu.Items.Add(refreshItem);
        menu.Items.Add(autoStartMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(copyDiagnosticsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(projectItem);
        menu.Items.Add(exitItem);
        return menu;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CS_DROPSHADOW;
            return parameters;
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            // 延后一轮消息再定位，确保无边框窗口完成句柄创建和初次布局。
            BeginInvoke((MethodInvoker)delegate { PositionMiniWindow(); });
            await RefreshQuotaAsync();
        }
        catch (Exception)
        {
            // OnShown 属于 async void 入口，最后一层兜底避免异常直接终止消息循环。
            if (!cancellation.IsCancellationRequested)
            {
                isRefreshing = false;
                snapshot = QuotaSnapshot.Failure("查询失败", "状态栏启动失败，请点击刷新重试");
                toolTip.SetToolTip(this, BuildTooltipText());
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

        NativeRect workArea;
        if (!SystemParametersInfo(SpiGetWorkArea, 0, out workArea, 0))
        {
            Rectangle fallback = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                Math.Max(fallback.Left + 4, fallback.Right - Width - 16),
                Math.Max(fallback.Top + 4, fallback.Bottom - Height - 16));
            return;
        }

        int left = Math.Max(workArea.Left + 4, workArea.Right - Width - 16);
        int top = Math.Max(workArea.Top + 4, workArea.Bottom - Height - 16);
        SetWindowPos(Handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refreshTimer.Stop();
        cancellation.Cancel();
        quotaService.Dispose();
        cancellation.Dispose();
        toolTip.Dispose();
        contextMenu.Dispose();
        base.OnFormClosed(e);
    }

    private async Task RefreshQuotaAsync()
    {
        if (isRefreshing || cancellation.IsCancellationRequested)
        {
            return;
        }

        isRefreshing = true;
        Invalidate();
        try
        {
            QuotaSnapshot result = await quotaService.QueryAsync(cancellation.Token);
            if (!cancellation.IsCancellationRequested)
            {
                snapshot = result;
            }
        }
        catch (Exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                snapshot = QuotaSnapshot.Failure("查询失败", "状态栏未能完成刷新");
            }
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                isRefreshing = false;
                toolTip.SetToolTip(this, BuildTooltipText());
                Invalidate();
                // 某些无边框窗口管理器会在异步首帧后重置位置，查询完成后再校正一次。
                PositionMiniWindow();
            }
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
                snapshot = QuotaSnapshot.Failure("查询失败", "状态栏未能完成刷新");
                toolTip.SetToolTip(this, BuildTooltipText());
                Invalidate();
            }
        }
    }

    /// <summary>
    /// 汇总可安全提交到 Issue 的运行信息。这里明确排除令牌、账户标识、代理地址和完整响应。
    /// </summary>
    private string BuildDiagnosticReport()
    {
        StringBuilder report = new StringBuilder();
        report.AppendLine("ChatGPT/Codex 状态栏诊断");
        report.AppendLine();
        report.AppendLine("系统：" + Environment.OSVersion.VersionString);
        report.AppendLine("运行时：.NET " + Environment.Version.ToString());
        report.AppendLine("进程：" + (IntPtr.Size * 8).ToString(CultureInfo.InvariantCulture) + " 位");
        report.AppendLine(quotaService.GetCredentialDiagnostic());
        report.AppendLine(quotaService.GetProxyDiagnostic());
        report.AppendLine("查询状态：" + snapshot.StatusText);
        report.AppendLine("计划显示：" + snapshot.PlanName);
        report.AppendLine("额度窗口：" + (snapshot.Windows == null ? 0 : snapshot.Windows.Count).ToString(CultureInfo.InvariantCulture));
        report.AppendLine("最近查询：" + (snapshot.QueriedAt.HasValue
            ? snapshot.QueriedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "未查询"));
        report.AppendLine("开机自启：" + (autoStartEnabled ? "已开启" : "已关闭"));
        if (!string.IsNullOrWhiteSpace(autoStartError))
        {
            report.AppendLine("启动项：" + autoStartError);
        }
        if (!snapshot.Success && !string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            report.AppendLine("错误：" + snapshot.ErrorText);
        }
        report.AppendLine();
        report.AppendLine("诊断信息不包含 Token、账户 ID、代理地址或完整响应。");
        return report.ToString();
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
        MessageBox.Show(this, BuildDiagnosticReport(), "诊断信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show(this, "无法访问剪贴板，请使用“运行诊断”查看信息。", "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (LinearGradientBrush background = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(31, 38, 49),
            Color.FromArgb(14, 18, 25),
            8f))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        using (Pen border = new Pen(Color.FromArgb(54, 64, 78), 1f))
        using (GraphicsPath borderPath = RoundedRectangle(new Rectangle(0, 0, WindowWidth - 1, WindowHeight - 1), 9))
        {
            g.DrawPath(border, borderPath);
        }

        DrawBrand(g);
        DrawDivider(g, 58);
        DrawWindow(g, new Rectangle(68, 0, 58, WindowHeight), FindWindow(18000), "5h", Color.FromArgb(165, 255, 117));
        DrawDivider(g, 126);
        DrawWindow(g, new Rectangle(136, 0, 58, WindowHeight), FindWindow(604800), "7d", Color.FromArgb(111, 196, 255));
        DrawDivider(g, 194);
        DrawStatus(g);
        DrawActionButtons(g);
    }

    private void DrawBrand(Graphics g)
    {
        Color statusColor = snapshot.Success ? Color.FromArgb(165, 255, 117) : Color.FromArgb(255, 190, 96);
        using (SolidBrush dot = new SolidBrush(statusColor))
        {
            g.FillEllipse(dot, 8, 16, 7, 7);
        }

        DrawText(g, CompactPlanName(), "Microsoft YaHei UI", 8.2f, FontStyle.Bold, Color.FromArgb(244, 246, 248), 20, 9);
    }

    private string CompactPlanName()
    {
        if (!string.IsNullOrWhiteSpace(snapshot.PlanName) &&
            snapshot.PlanName.IndexOf("Plus", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "GPT+";
        }
        return "GPT";
    }

    private void DrawWindow(Graphics g, Rectangle bounds, QuotaWindow window, string compactName, Color accent)
    {
        double usedPercent = window == null ? 0d : window.UsedPercent;
        string percentage = window == null ? "--" : usedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        int trackWidth = bounds.Width - 6;
        int fillWidth = (int)Math.Round(trackWidth * usedPercent / 100d);
        fillWidth = Math.Max(0, Math.Min(trackWidth, fillWidth));

        DrawText(g, compactName, "Consolas", 7f, FontStyle.Bold, Color.FromArgb(183, 193, 207), bounds.Left, 11);
        DrawTextRight(g, percentage, "Consolas", 8.5f, FontStyle.Bold, Color.FromArgb(247, 248, 250), bounds.Right - 2, 9);

        Rectangle track = new Rectangle(bounds.Left, 27, trackWidth, 2);
        using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(49, 57, 70)))
        using (GraphicsPath trackPath = RoundedRectangle(track, 2))
        {
            g.FillPath(trackBrush, trackPath);
        }
        if (fillWidth > 0)
        {
            Rectangle fill = new Rectangle(track.Left, track.Top, fillWidth, track.Height);
            using (SolidBrush fillBrush = new SolidBrush(accent))
            using (GraphicsPath fillPath = RoundedRectangle(fill, 2))
            {
                g.FillPath(fillBrush, fillPath);
            }
        }

        // 底部直接显示日期和下一次重置时间；扩大额度列宽度后避免日期与分隔线重叠。
        string reset = window == null ? "--" : FormatVisibleReset(window.ResetAt);
        DrawText(g, reset, "Consolas", 6.5f, FontStyle.Bold, Color.FromArgb(190, 202, 219), bounds.Left, 28);
    }

    private void DrawStatus(Graphics g)
    {
        Color statusColor = snapshot.Success ? Color.FromArgb(165, 255, 117) : Color.FromArgb(255, 190, 96);
        using (SolidBrush dot = new SolidBrush(statusColor))
        {
            g.FillEllipse(dot, 203, 16, 5, 5);
        }

        string status = isRefreshing ? "..." : (snapshot.Success ? "OK" : "ERR");
        DrawText(g, status, "Consolas", 7.5f, FontStyle.Bold, statusColor, 212, 11);
    }

    private void DrawActionButtons(Graphics g)
    {
        bool refreshHover = refreshArea.Contains(PointToClient(Cursor.Position));
        bool closeHover = closeArea.Contains(PointToClient(Cursor.Position));
        DrawButtonSurface(g, refreshArea, refreshHover);
        DrawButtonSurface(g, closeArea, closeHover);

        using (Pen refreshPen = new Pen(Color.FromArgb(171, 182, 196), 1.4f))
        {
            refreshPen.StartCap = LineCap.Round;
            refreshPen.EndCap = LineCap.Round;
            g.DrawArc(refreshPen, new Rectangle(refreshArea.Left + 4, refreshArea.Top + 4, 10, 10), 35, 285);
            Point[] arrow =
            {
                new Point(refreshArea.Left + 14, refreshArea.Top + 5),
                new Point(refreshArea.Left + 15, refreshArea.Top + 9),
                new Point(refreshArea.Left + 11, refreshArea.Top + 8)
            };
            g.DrawLines(refreshPen, arrow);
        }

        using (Pen closePen = new Pen(Color.FromArgb(171, 182, 196), 1.5f))
        {
            closePen.StartCap = LineCap.Round;
            closePen.EndCap = LineCap.Round;
            g.DrawLine(closePen, closeArea.Left + 5, closeArea.Top + 5, closeArea.Right - 5, closeArea.Bottom - 5);
            g.DrawLine(closePen, closeArea.Right - 5, closeArea.Top + 5, closeArea.Left + 5, closeArea.Bottom - 5);
        }
    }

    private static void DrawButtonSurface(Graphics g, Rectangle area, bool hover)
    {
        using (SolidBrush brush = new SolidBrush(hover ? Color.FromArgb(52, 62, 75) : Color.Transparent))
        using (GraphicsPath path = RoundedRectangle(area, 7))
        {
            g.FillPath(brush, path);
        }
    }

    private QuotaWindow FindWindow(int seconds)
    {
        foreach (QuotaWindow window in snapshot.Windows)
        {
            if (window.LimitWindowSeconds == seconds)
            {
                return window;
            }
        }
        return null;
    }

    private string BuildTooltipText()
    {
        string text = snapshot.PlanName + "\r\n" + snapshot.AccountLabel;
        foreach (QuotaWindow window in snapshot.Windows)
        {
            text += "\r\n" + window.Name + ": " + window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%，重置 " + FormatCompactReset(window.ResetAt);
        }
        if (!snapshot.Success && !string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            text += "\r\n" + snapshot.ErrorText;
        }
        text += "\r\n右键：选项、开机自启和诊断";
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

    private static void DrawDivider(Graphics g, int x)
    {
        using (Pen divider = new Pen(Color.FromArgb(45, 53, 65)))
        {
            g.DrawLine(divider, x, 7, x, WindowHeight - 7);
        }
    }

    private static void DrawText(Graphics g, string text, string family, float size, FontStyle style, Color color, float x, float y)
    {
        using (Font font = new Font(family, size, style, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.DrawString(text ?? string.Empty, font, brush, x, y);
        }
    }

    private static void DrawTextRight(Graphics g, string text, string family, float size, FontStyle style, Color color, float right, float y)
    {
        using (Font font = new Font(family, size, style, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(color))
        {
            SizeF measured = g.MeasureString(text ?? string.Empty, font);
            g.DrawString(text ?? string.Empty, font, brush, right - measured.Width, y);
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
        if (e.Button == MouseButtons.Left && !closeArea.Contains(e.Location) && !refreshArea.Contains(e.Location))
        {
            userMoved = true;
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }
    }

    protected override async void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (closeArea.Contains(e.Location))
        {
            Close();
            return;
        }
        if (refreshArea.Contains(e.Location))
        {
            await RefreshQuotaSafelyAsync();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = closeArea.Contains(e.Location) || refreshArea.Contains(e.Location) ? Cursors.Hand : Cursors.SizeAll;
        Invalidate(new Rectangle(WindowWidth - 52, 6, 48, 28));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        Invalidate(new Rectangle(WindowWidth - 52, 6, 48, 28));
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
    [STAThread]
    private static void Main()
    {
        // 开机自启和手动启动可能同时发生，使用本地互斥体确保屏幕上只有一个状态栏。
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Local\\ChatGPTCodexUsageStatusBar", out createdNew))
        {
            if (!createdNew)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new StatusWindow());
        }
    }
}
