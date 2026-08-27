using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

/// <summary>
/// AI Usage Hub 大屏工作区。状态栏只负责入口，这里承载所有额度窗口、数据新鲜度、趋势和常用操作。
/// 窗口只接收脱敏快照与本地历史，不读取 OAuth 文件，也不保存接口原文。
/// </summary>
internal sealed class UsageHubForm : Form
{
    private readonly Func<Task<UsageSnapshot>> refreshAction;
    private readonly Func<IList<HistoryPoint>> historyLoader;
    private readonly Action showSettings;
    private readonly Action showDiagnostics;
    private readonly Action openProject;
    private readonly Action copySummary;
    private readonly Action exportHistory;
    private readonly ThemePalette palette;
    private readonly UsageHubSurface surface;
    private readonly FlowLayoutPanel actionBar;
    private readonly Button closeButton;
    private readonly Button expandButton;
    private readonly Button refreshButton;
    private readonly Timer animationTimer;
    private UsageSnapshot snapshot;
    private IList<HistoryPoint> history;
    private bool isRefreshing;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public UsageHubForm(
        UsageSnapshot initialSnapshot,
        IList<HistoryPoint> initialHistory,
        Func<Task<UsageSnapshot>> refreshAction,
        Func<IList<HistoryPoint>> historyLoader,
        Action showSettings,
        Action showDiagnostics,
        Action openProject,
        Action copySummary,
        Action exportHistory,
        ThemeMode theme,
        bool animationsEnabled)
    {
        this.refreshAction = refreshAction;
        this.historyLoader = historyLoader;
        this.showSettings = showSettings;
        this.showDiagnostics = showDiagnostics;
        this.openProject = openProject;
        this.copySummary = copySummary;
        this.exportHistory = exportHistory;
        palette = ThemePalette.Create(theme);
        snapshot = initialSnapshot == null
            ? UsageSnapshot.Loading("chatgpt-codex")
            : initialSnapshot.Clone();
        history = initialHistory == null
            ? new List<HistoryPoint>()
            : new List<HistoryPoint>(initialHistory);

        Text = "Usage Hub";
        ClientSize = new Size(940, 640);
        // 预留趋势卡、底部状态线和操作条的独立空间，避免缩放到最小尺寸时相互覆盖。
        // 底部操作条包含八个固定宽度入口，保留足够横向空间避免最小化后按钮被裁掉。
        MinimumSize = new Size(860, 620);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = false;
        BackColor = palette.BackgroundBottom;
        ForeColor = palette.PrimaryText;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        KeyPreview = true;

        surface = new UsageHubSurface(palette, animationsEnabled);
        surface.Dock = DockStyle.Fill;
        surface.SetData(snapshot, history);
        surface.MouseDown += SurfaceMouseDown;
        surface.DoubleClick += delegate(object sender, EventArgs args) { ToggleMaximized(); };
        Controls.Add(surface);

        closeButton = CreateChromeButton("×", "关闭 Usage Hub");
        closeButton.Click += delegate(object sender, EventArgs args) { Close(); };
        surface.Controls.Add(closeButton);

        expandButton = CreateChromeButton("□", "切换大屏尺寸");
        expandButton.Click += delegate(object sender, EventArgs args) { ToggleMaximized(); };
        surface.Controls.Add(expandButton);

        actionBar = new FlowLayoutPanel();
        actionBar.FlowDirection = FlowDirection.LeftToRight;
        actionBar.WrapContents = false;
        actionBar.BackColor = Color.Transparent;
        actionBar.Padding = new Padding(0);
        actionBar.Margin = new Padding(0);
        surface.Controls.Add(actionBar);

        refreshButton = CreateActionButton("刷新数据", true, 104);
        refreshButton.Click += RefreshButtonClick;
        actionBar.Controls.Add(refreshButton);

        Button settingsButton = CreateActionButton("设置", false, 76);
        settingsButton.Click += delegate(object sender, EventArgs args) { CloseThen(showSettings); };
        actionBar.Controls.Add(settingsButton);

        Button diagnosticsButton = CreateActionButton("诊断中心", false, 96);
        diagnosticsButton.Click += delegate(object sender, EventArgs args) { CloseThen(showDiagnostics); };
        actionBar.Controls.Add(diagnosticsButton);

        Button copyButton = CreateActionButton("复制摘要", false, 72);
        copyButton.Click += delegate(object sender, EventArgs args) { CloseThen(copySummary); };
        actionBar.Controls.Add(copyButton);

        Button exportButton = CreateActionButton("导出趋势", false, 80);
        exportButton.Click += delegate(object sender, EventArgs args) { CloseThen(exportHistory); };
        actionBar.Controls.Add(exportButton);

        Button projectButton = CreateActionButton("项目主页", false, 96);
        projectButton.Click += delegate(object sender, EventArgs args) { CloseThen(openProject); };
        actionBar.Controls.Add(projectButton);

        Button backButton = CreateActionButton("回到状态栏", false, 112);
        backButton.Click += delegate(object sender, EventArgs args) { Close(); };
        actionBar.Controls.Add(backButton);

        Button exitButton = CreateActionButton("退出程序", false, 92);
        exitButton.ForeColor = palette.Error;
        exitButton.FlatAppearance.BorderColor = UiTheme.WithAlpha(palette.Error, 160);
        exitButton.Click += delegate(object sender, EventArgs args) { CloseThen(delegate { Application.Exit(); }); };
        actionBar.Controls.Add(exitButton);

        animationTimer = new Timer();
        // 关闭动效时仍需要低频重绘倒计时，但不应保持高频动画帧。
        animationTimer.Interval = animationsEnabled ? 33 : 1000;
        animationTimer.Tick += AnimationTimerTick;
        Resize += delegate(object sender, EventArgs args) { LayoutSurfaceControls(); };
        KeyDown += UsageHubFormKeyDown;
        Shown += UsageHubFormShown;
        FormClosed += UsageHubFormClosed;
        LayoutSurfaceControls();
    }

    /// <summary>
    /// 为反射 smoke 和旧调用保留一个轻量构造入口，默认使用深色主题和完整动效。
    /// </summary>
    public UsageHubForm(
        UsageSnapshot initialSnapshot,
        IList<HistoryPoint> initialHistory,
        Func<Task<UsageSnapshot>> refreshAction,
        Func<IList<HistoryPoint>> historyLoader)
        : this(
            initialSnapshot,
            initialHistory,
            refreshAction,
            historyLoader,
            null,
            null,
            null,
            null,
            null,
            ThemeMode.Dark,
            true)
    {
    }

    /// <summary>
    /// 兼容旧的九参数调用方；新入口可额外注入摘要复制和历史导出动作。
    /// </summary>
    public UsageHubForm(
        UsageSnapshot initialSnapshot,
        IList<HistoryPoint> initialHistory,
        Func<Task<UsageSnapshot>> refreshAction,
        Func<IList<HistoryPoint>> historyLoader,
        Action showSettings,
        Action showDiagnostics,
        Action openProject,
        ThemeMode theme,
        bool animationsEnabled)
        : this(
            initialSnapshot,
            initialHistory,
            refreshAction,
            historyLoader,
            showSettings,
            showDiagnostics,
            openProject,
            null,
            null,
            theme,
            animationsEnabled)
    {
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            const int CsDropshadow = 0x00020000;
            const int WsExToolwindow = 0x00000080;
            parameters.ClassStyle |= CsDropshadow;
            // 大屏属于工作区工具窗，不在 Alt+Tab 和任务栏创建重复入口。
            parameters.ExStyle |= WsExToolwindow;
            return parameters;
        }
    }

    private static Button CreateChromeButton(string text, string accessibleName)
    {
        Button button = new Button();
        button.Text = text;
        button.Width = 38;
        button.Height = 34;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.Transparent;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Symbol", 13f, FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.AccessibleName = accessibleName;
        return button;
    }

    private Button CreateActionButton(string text, bool primary, int width)
    {
        Button button = new Button();
        button.Text = text;
        button.Width = width;
        button.Height = 38;
        button.AccessibleName = text;
        button.Margin = new Padding(2, 0, 2, 0);
        UiTheme.StyleButton(button, palette, primary);
        return button;
    }

    private void LayoutSurfaceControls()
    {
        if (surface == null)
        {
            return;
        }

        closeButton.Location = new Point(Math.Max(0, surface.ClientSize.Width - 50), 16);
        expandButton.Location = new Point(Math.Max(0, surface.ClientSize.Width - 92), 16);
        actionBar.Location = new Point(34, Math.Max(0, surface.ClientSize.Height - 54));
        actionBar.Size = new Size(Math.Max(400, surface.ClientSize.Width - 68), 40);
        closeButton.BringToFront();
        expandButton.BringToFront();
        actionBar.BringToFront();
    }

    private void UsageHubFormShown(object sender, EventArgs e)
    {
        surface.BeginEntrance();
        animationTimer.Start();
        Activate();
    }

    private void UsageHubFormClosed(object sender, FormClosedEventArgs e)
    {
        animationTimer.Stop();
        animationTimer.Dispose();
    }

    private void AnimationTimerTick(object sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }
        surface.AdvanceAnimation();
    }

    private void SurfaceMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.Y > 82 || closeButton.Bounds.Contains(e.Location) || expandButton.Bounds.Contains(e.Location))
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void UsageHubFormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            Close();
            return;
        }
        if (e.KeyCode == Keys.F5)
        {
            e.SuppressKeyPress = true;
            RefreshButtonClick(this, EventArgs.Empty);
            return;
        }
        if (e.Control && e.KeyCode == Keys.C)
        {
            e.SuppressKeyPress = true;
            CloseThen(copySummary);
            return;
        }
        if (e.Control && e.KeyCode == Keys.E)
        {
            e.SuppressKeyPress = true;
            CloseThen(exportHistory);
        }
    }

    private void ToggleMaximized()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(940, 640);
            StartPosition = FormStartPosition.CenterScreen;
            CenterToScreen();
            return;
        }

        Screen screen = Screen.FromControl(this);
        if (screen != null)
        {
            MaximizedBounds = screen.WorkingArea;
        }
        WindowState = FormWindowState.Maximized;
    }

    private void CloseThen(Action action)
    {
        Close();
        if (action != null)
        {
            action();
        }
    }

    private async void RefreshButtonClick(object sender, EventArgs e)
    {
        if (refreshAction == null || isRefreshing)
        {
            return;
        }

        isRefreshing = true;
        refreshButton.Enabled = false;
        refreshButton.Text = "刷新中";
        surface.SetRefreshing(true);
        try
        {
            UsageSnapshot refreshed = await refreshAction();
            if (refreshed != null && !IsDisposed)
            {
                snapshot = refreshed.Clone();
                IList<HistoryPoint> refreshedHistory = historyLoader == null ? history : historyLoader();
                history = refreshedHistory == null ? new List<HistoryPoint>() : new List<HistoryPoint>(refreshedHistory);
                surface.SetData(snapshot, history);
            }
        }
        catch (Exception)
        {
            // 主窗口已经把异常收敛成安全状态；工作区只需要保持可用并恢复按钮。
        }
        finally
        {
            if (!IsDisposed)
            {
                isRefreshing = false;
                refreshButton.Enabled = true;
                refreshButton.Text = "刷新数据";
                surface.SetRefreshing(false);
            }
        }
    }
}

/// <summary>
/// Usage Hub 的双缓冲绘图区。它把动画限制在重绘和变换属性上，数据文字仍然使用即时真实值。
/// </summary>
internal sealed class UsageHubSurface : Control
{
    private readonly ThemePalette palette;
    private readonly bool animationsEnabled;
    private UsageSnapshot snapshot;
    private IList<HistoryPoint> history = new List<HistoryPoint>();
    private IList<UsageInsight> insights = new List<UsageInsight>();
    private string healthLabel = "读取中";
    private double primaryTarget;
    private double secondaryTarget;
    private double primaryAnimated;
    private double secondaryAnimated;
    private float phase;
    private float entrance;
    private bool refreshing;

    public UsageHubSurface(ThemePalette palette, bool animationsEnabled)
    {
        this.palette = palette ?? ThemePalette.Create(ThemeMode.Dark);
        this.animationsEnabled = animationsEnabled;
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = this.palette.BackgroundBottom;
        ForeColor = this.palette.PrimaryText;
    }

    public void SetData(UsageSnapshot value, IList<HistoryPoint> points)
    {
        snapshot = value == null ? UsageSnapshot.Loading("chatgpt-codex") : value.Clone();
        history = points == null ? new List<HistoryPoint>() : new List<HistoryPoint>(points);
        QuotaWindow primary = FindDisplayWindow(18000, 0, null);
        QuotaWindow secondary = FindDisplayWindow(604800, 1, primary);
        primaryTarget = GetPercent(primary);
        secondaryTarget = GetPercent(secondary);
        insights = UsageInsights.Build(snapshot, history, DateTimeOffset.UtcNow);
        healthLabel = UsageInsights.GetHealthLabel(snapshot, history, DateTimeOffset.UtcNow);
        if (!animationsEnabled || entrance <= 0f)
        {
            primaryAnimated = primaryTarget;
            secondaryAnimated = secondaryTarget;
        }
        Invalidate();
    }

    public void BeginEntrance()
    {
        entrance = animationsEnabled ? 0f : 1f;
        if (!animationsEnabled)
        {
            primaryAnimated = primaryTarget;
            secondaryAnimated = secondaryTarget;
        }
        Invalidate();
    }

    public void SetRefreshing(bool value)
    {
        refreshing = value;
        Invalidate();
    }

    public void AdvanceAnimation()
    {
        if (!animationsEnabled)
        {
            // FormatCountdown/FormatReset 在绘制时计算，低频重绘可让时间信息持续更新。
            Invalidate();
            return;
        }

        phase += refreshing ? 0.15f : 0.045f;
        if (phase > (float)(Math.PI * 2d))
        {
            phase -= (float)(Math.PI * 2d);
        }
        entrance = Math.Min(1f, entrance + 0.08f);
        primaryAnimated = Step(primaryAnimated, primaryTarget);
        secondaryAnimated = Step(secondaryAnimated, secondaryTarget);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawBackground(g);
        DrawHeader(g);
        DrawMetricCards(g);
        DrawTrendCard(g);
        DrawFooter(g);
    }

    private void DrawBackground(Graphics g)
    {
        using (LinearGradientBrush background = new LinearGradientBrush(
            ClientRectangle,
            palette.BackgroundTop,
            palette.BackgroundBottom,
            35f))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        using (Pen grid = new Pen(UiTheme.WithAlpha(palette.Grid, 56), 1f))
        {
            for (int x = 24; x < Width; x += 48)
            {
                g.DrawLine(grid, x, 0, x, Height - 70);
            }
            for (int y = 18; y < Height - 70; y += 36)
            {
                g.DrawLine(grid, 0, y, Width, y);
            }
        }

        using (Pen topLine = new Pen(UiTheme.WithAlpha(palette.PrimaryAccent, 210), 2f))
        {
            g.DrawLine(topLine, 30, 1, Width - 30, 1);
        }

        if (animationsEnabled)
        {
            float progress = (float)((Math.Sin(phase * 0.7f) + 1d) / 2d);
            int sweepX = 30 + (int)((Width - 60) * progress);
            using (Pen sweep = new Pen(UiTheme.WithAlpha(palette.SecondaryAccent, refreshing ? 115 : 48), 1f))
            {
                g.DrawLine(sweep, sweepX, 18, sweepX, Height - 78);
            }
        }
    }

    private void DrawHeader(Graphics g)
    {
        int alpha = GetEntranceAlpha();
        DrawText(g, "Usage Hub", "Microsoft YaHei UI", 23f, FontStyle.Bold, UiTheme.WithAlpha(palette.PrimaryText, alpha), 36, 24);
        string plan = snapshot == null || string.IsNullOrWhiteSpace(snapshot.PlanName) ? "ChatGPT / Codex" : "ChatGPT / Codex · " + snapshot.PlanName;
        DrawText(g, plan, "Microsoft YaHei UI", 9.5f, FontStyle.Regular, UiTheme.WithAlpha(palette.SecondaryText, alpha), 38, 58);
        DrawText(g, "健康度 · " + healthLabel, "Microsoft YaHei UI", 8.5f, FontStyle.Bold, UiTheme.WithAlpha(GetHealthColor(), alpha), 38, 77);

        string status = GetStatusText(snapshot);
        Color statusColor = GetStatusColor(snapshot);
        SizeF statusSize;
        using (Font font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
        {
            statusSize = g.MeasureString(status, font);
        }
        int pillWidth = (int)Math.Ceiling(statusSize.Width) + 34;
        Rectangle pill = new Rectangle(Width - pillWidth - 38, 34, pillWidth, 30);
        using (GraphicsPath path = RoundedRectangle(pill, 15))
        using (SolidBrush fill = new SolidBrush(UiTheme.WithAlpha(statusColor, 28)))
        using (Pen border = new Pen(UiTheme.WithAlpha(statusColor, 145), 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        using (SolidBrush dot = new SolidBrush(statusColor))
        {
            float pulse = animationsEnabled ? (float)((Math.Sin(phase * 1.5f) + 1d) * 1.5d) : 0f;
            g.FillEllipse(dot, pill.Left + 12 - pulse, pill.Top + 12 - pulse, 6 + pulse * 2f, 6 + pulse * 2f);
        }
        DrawText(g, status, "Microsoft YaHei UI", 9f, FontStyle.Bold, statusColor, pill.Left + 24, pill.Top + 7);
    }

    private void DrawMetricCards(Graphics g)
    {
        int contentWidth = Math.Max(520, Width - 72);
        int gap = 20;
        int cardWidth = Math.Max(250, (contentWidth - gap) / 2);
        int top = 112;
        int cardHeight = 188;
        QuotaWindow primary = FindDisplayWindow(18000, 0, null);
        QuotaWindow secondary = FindDisplayWindow(604800, 1, primary);
        DrawMetricCard(g, new Rectangle(36, top, cardWidth, cardHeight), primary, FindInsight(primary), palette.PrimaryAccent, primaryAnimated, "PRIMARY");
        DrawMetricCard(g, new Rectangle(36 + cardWidth + gap, top, cardWidth, cardHeight), secondary, FindInsight(secondary), palette.SecondaryAccent, secondaryAnimated, "SECONDARY");
    }

    private void DrawMetricCard(Graphics g, Rectangle bounds, QuotaWindow window, UsageInsight insight, Color accent, double animatedPercent, string badge)
    {
        using (GraphicsPath cardPath = RoundedRectangle(bounds, 14))
        using (SolidBrush cardBrush = new SolidBrush(UiTheme.WithAlpha(palette.Surface, 238)))
        using (Pen cardBorder = new Pen(UiTheme.WithAlpha(palette.ControlBorder, 205), 1f))
        {
            g.FillPath(cardBrush, cardPath);
            g.DrawPath(cardBorder, cardPath);
        }

        using (SolidBrush badgeBrush = new SolidBrush(UiTheme.WithAlpha(accent, 34)))
        using (GraphicsPath badgePath = RoundedRectangle(new Rectangle(bounds.Left + 18, bounds.Top + 17, 72, 22), 11))
        {
            g.FillPath(badgeBrush, badgePath);
        }
        DrawText(g, badge, "Consolas", 7f, FontStyle.Bold, accent, bounds.Left + 29, bounds.Top + 22);

        int centerX = bounds.Left + 90;
        int centerY = bounds.Top + 104;
        int radius = 55;
        using (Pen track = new Pen(UiTheme.WithAlpha(palette.Track, 190), 9f))
        using (Pen glow = new Pen(UiTheme.WithAlpha(accent, 38), 15f))
        using (Pen progress = new Pen(GetUsageColor(window, accent), 9f))
        {
            track.StartCap = LineCap.Round;
            track.EndCap = LineCap.Round;
            progress.StartCap = LineCap.Round;
            progress.EndCap = LineCap.Round;
            Rectangle ring = new Rectangle(centerX - radius, centerY - radius, radius * 2, radius * 2);
            g.DrawArc(track, ring, -90f, 300f);
            float sweep = (float)(300d * Math.Max(0d, Math.Min(100d, animatedPercent)) / 100d);
            if (sweep > 0f)
            {
                if (animationsEnabled)
                {
                    g.DrawArc(glow, ring, -90f, sweep);
                }
                g.DrawArc(progress, ring, -90f, sweep);
            }
            if (refreshing && animationsEnabled)
            {
                using (Pen scan = new Pen(UiTheme.WithAlpha(Color.White, 150), 2f))
                {
                    g.DrawArc(scan, ring, -90f + phase * 24f, 20f);
                }
            }
        }

        string percentage = window == null ? "--" : window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%";
        DrawTextCentered(g, percentage, "Consolas", 21f, FontStyle.Bold, palette.PrimaryText, centerX, centerY - 10);
        DrawTextCentered(g, "已使用", "Microsoft YaHei UI", 7.5f, FontStyle.Regular, palette.SecondaryText, centerX, centerY + 18);

        int textLeft = bounds.Left + 166;
        string title = window == null ? "额度窗口" : window.Name;
        DrawText(g, title, "Microsoft YaHei UI", 12f, FontStyle.Bold, palette.PrimaryText, textLeft, bounds.Top + 44);
        if (window == null)
        {
            DrawText(g, "等待首次成功刷新", "Microsoft YaHei UI", 9f, FontStyle.Regular, palette.SecondaryText, textLeft, bounds.Top + 76);
            return;
        }

        double remaining = Math.Max(0d, 100d - window.UsedPercent);
        DrawText(g, "剩余 " + remaining.ToString("0.#", CultureInfo.InvariantCulture) + "%", "Consolas", 10f, FontStyle.Bold, GetUsageColor(window, accent), textLeft, bounds.Top + 74);
        DrawText(g, "下次重置", "Microsoft YaHei UI", 8f, FontStyle.Regular, palette.SecondaryText, textLeft, bounds.Top + 104);
        DrawText(g, FormatReset(window.ResetAt), "Consolas", 10f, FontStyle.Bold, palette.PrimaryText, textLeft, bounds.Top + 121);
        DrawText(g, FormatCountdown(window.ResetAt), "Microsoft YaHei UI", 8f, FontStyle.Regular, palette.SecondaryText, textLeft, bounds.Top + 145);
        DrawText(g, insight == null ? "等待历史趋势" : insight.GetRateText(), "Consolas", 7.5f, FontStyle.Bold, GetInsightColor(insight), textLeft, bounds.Top + 161);
        if (insight != null && insight.ProjectedExhaustionAt.HasValue)
        {
            DrawText(g, "预计 " + FormatForecast(insight.ProjectedExhaustionAt), "Microsoft YaHei UI", 7.5f, FontStyle.Regular, palette.SecondaryText, textLeft, bounds.Top + 176);
        }
    }

    private void DrawTrendCard(Graphics g)
    {
        Rectangle bounds = new Rectangle(36, 322, Math.Max(520, Width - 72), 196);
        using (GraphicsPath cardPath = RoundedRectangle(bounds, 14))
        using (SolidBrush cardBrush = new SolidBrush(UiTheme.WithAlpha(palette.Surface, 238)))
        using (Pen cardBorder = new Pen(UiTheme.WithAlpha(palette.ControlBorder, 205), 1f))
        {
            g.FillPath(cardBrush, cardPath);
            g.DrawPath(cardBorder, cardPath);
        }
        DrawText(g, "使用趋势", "Microsoft YaHei UI", 12f, FontStyle.Bold, palette.PrimaryText, bounds.Left + 18, bounds.Top + 16);
        DrawText(g, "本机历史 · 仅保存脱敏百分比", "Microsoft YaHei UI", 8f, FontStyle.Regular, palette.SecondaryText, bounds.Left + 18, bounds.Top + 38);

        Rectangle plot = new Rectangle(bounds.Left + 74, bounds.Top + 56, Math.Max(200, bounds.Width - 98), 112);
        using (Pen grid = new Pen(UiTheme.WithAlpha(palette.Grid, 155), 1f))
        using (SolidBrush labels = new SolidBrush(palette.SecondaryText))
        using (Font labelFont = new Font("Consolas", 7f))
        {
            for (int step = 0; step <= 4; step++)
            {
                float y = plot.Top + plot.Height * step / 4f;
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                g.DrawString((100 - step * 25).ToString(CultureInfo.InvariantCulture), labelFont, labels, bounds.Left + 34, y - 6);
            }
            for (int step = 0; step <= 6; step++)
            {
                float x = plot.Left + plot.Width * step / 6f;
                g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
            }
        }

        List<int> windows = GetSeriesWindows();
        Color[] colors = { palette.PrimaryAccent, palette.SecondaryAccent, palette.Success };
        bool hasSeries = false;
        for (int index = 0; index < windows.Count && index < colors.Length; index++)
        {
            hasSeries = DrawSeries(g, plot, windows[index], colors[index], index) || hasSeries;
        }
        if (hasSeries && animationsEnabled)
        {
            DrawTrendCursor(g, plot);
        }
        if (!hasSeries)
        {
            DrawTextCentered(g, "成功刷新两次后，这里会绘制真实趋势", "Microsoft YaHei UI", 9f, FontStyle.Regular, palette.SecondaryText, plot.Left + plot.Width / 2, plot.Top + plot.Height / 2 - 6);
        }

        int legendX = bounds.Right - 224;
        for (int index = 0; index < windows.Count && index < colors.Length; index++)
        {
            using (SolidBrush dot = new SolidBrush(colors[index]))
            {
                g.FillEllipse(dot, legendX, bounds.Top + 20, 6, 6);
            }
            DrawText(g, FormatWindowLabel(windows[index]), "Consolas", 7.5f, FontStyle.Bold, colors[index], legendX + 10, bounds.Top + 17);
            legendX += 56;
        }
    }

    private void DrawTrendCursor(Graphics g, Rectangle plot)
    {
        float progress = (float)((Math.Sin(phase * 0.55f) + 1d) / 2d);
        float x = plot.Left + plot.Width * progress;
        using (Pen cursor = new Pen(UiTheme.WithAlpha(palette.SecondaryAccent, 110), 1f))
        using (SolidBrush marker = new SolidBrush(UiTheme.WithAlpha(palette.SecondaryAccent, 175)))
        {
            g.DrawLine(cursor, x, plot.Top, x, plot.Bottom);
            g.FillEllipse(marker, x - 3, plot.Top - 3, 6, 6);
        }
    }

    private void DrawFooter(Graphics g)
    {
        int y = Height - 82;
        using (Pen line = new Pen(UiTheme.WithAlpha(palette.Divider, 180), 1f))
        {
            g.DrawLine(line, 34, y - 10, Width - 34, y - 10);
        }
        string last = snapshot != null && snapshot.LastLiveAt.HasValue
            ? "本机趋势 · 最近成功 " + snapshot.LastLiveAt.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : "尚未成功刷新";
        DrawText(g, last, "Consolas", 8f, FontStyle.Regular, palette.SecondaryText, 36, y + 11);
        DrawText(g, "v" + UpdateService.CurrentVersion, "Consolas", 8f, FontStyle.Bold, UiTheme.WithAlpha(palette.PrimaryAccent, 210), Width - 90, y + 17);
    }

    private bool DrawSeries(Graphics g, Rectangle plot, int seconds, Color color, int seriesIndex)
    {
        List<HistoryPoint> series = new List<HistoryPoint>();
        foreach (HistoryPoint point in history)
        {
            if (point != null && point.LimitWindowSeconds == seconds)
            {
                series.Add(point);
            }
        }
        series.Sort(delegate(HistoryPoint left, HistoryPoint right) { return left.ObservedAt.CompareTo(right.ObservedAt); });
        if (series.Count < 2)
        {
            return false;
        }

        DateTimeOffset first = series[0].ObservedAt;
        DateTimeOffset last = series[series.Count - 1].ObservedAt;
        double totalSeconds = Math.Max(1d, (last - first).TotalSeconds);
        int visibleCount = animationsEnabled
            ? Math.Max(2, Math.Min(series.Count, (int)Math.Ceiling(series.Count * Math.Max(0.35f, entrance))))
            : series.Count;
        List<PointF> points = new List<PointF>();
        for (int index = 0; index < visibleCount; index++)
        {
            HistoryPoint point = series[index];
            double ratio = (point.ObservedAt - first).TotalSeconds / totalSeconds;
            float x = plot.Left + (float)(Math.Max(0d, Math.Min(1d, ratio)) * plot.Width);
            float y = plot.Bottom - (float)(Math.Max(0d, Math.Min(100d, point.UsedPercent)) / 100d * plot.Height);
            points.Add(new PointF(x, y));
        }
        using (Pen line = new Pen(color, 2.2f))
        using (SolidBrush dot = new SolidBrush(color))
        {
            line.StartCap = LineCap.Round;
            line.EndCap = LineCap.Round;
            line.LineJoin = LineJoin.Round;
            if (points.Count > 1)
            {
                g.DrawLines(line, points.ToArray());
            }
            foreach (PointF point in points)
            {
                g.FillEllipse(dot, point.X - 3, point.Y - 3, 6, 6);
                using (Pen halo = new Pen(UiTheme.WithAlpha(color, 45), 3f))
                {
                    g.DrawEllipse(halo, point.X - 5, point.Y - 5, 10, 10);
                }
            }
        }
        return true;
    }

    private List<int> GetSeriesWindows()
    {
        HashSet<int> available = new HashSet<int>();
        foreach (HistoryPoint point in history)
        {
            if (point != null && point.LimitWindowSeconds > 0)
            {
                available.Add(point.LimitWindowSeconds);
            }
        }
        List<int> ordered = new List<int>();
        AddPreferred(available, ordered, 18000);
        AddPreferred(available, ordered, 604800);
        List<int> remaining = new List<int>(available);
        remaining.Sort();
        foreach (int value in remaining)
        {
            if (!ordered.Contains(value))
            {
                ordered.Add(value);
            }
        }
        return ordered;
    }

    private static void AddPreferred(HashSet<int> available, IList<int> ordered, int seconds)
    {
        if (available.Contains(seconds))
        {
            ordered.Add(seconds);
        }
    }

    private QuotaWindow FindDisplayWindow(int seconds, int fallbackIndex, QuotaWindow excluded)
    {
        List<QuotaWindow> converted = ConvertWindows(snapshot == null ? null : snapshot.Windows);
        foreach (QuotaWindow value in converted)
        {
            if (value.LimitWindowSeconds == seconds && !SameWindow(value, excluded))
            {
                return value;
            }
        }
        if (fallbackIndex >= 0 && fallbackIndex < converted.Count && !SameWindow(converted[fallbackIndex], excluded))
        {
            return converted[fallbackIndex];
        }
        foreach (QuotaWindow value in converted)
        {
            if (!SameWindow(value, excluded))
            {
                return value;
            }
        }
        return null;
    }

    private static bool SameWindow(QuotaWindow first, QuotaWindow second)
    {
        return first != null && second != null && first.LimitWindowSeconds == second.LimitWindowSeconds;
    }

    private static List<QuotaWindow> ConvertWindows(IList<UsageWindow> windows)
    {
        List<QuotaWindow> converted = new List<QuotaWindow>();
        if (windows == null)
        {
            return converted;
        }
        foreach (UsageWindow window in windows)
        {
            if (window != null)
            {
                converted.Add(new QuotaWindow(window.DisplayName, window.LimitWindowSeconds, window.UsedPercent, window.ResetAt));
            }
        }
        return converted;
    }

    private static double GetPercent(QuotaWindow window)
    {
        return window == null ? 0d : window.UsedPercent;
    }

    private UsageInsight FindInsight(QuotaWindow window)
    {
        if (window == null || insights == null)
        {
            return null;
        }
        foreach (UsageInsight insight in insights)
        {
            if (insight != null && insight.LimitWindowSeconds == window.LimitWindowSeconds)
            {
                return insight;
            }
        }
        return null;
    }

    private Color GetHealthColor()
    {
        if (snapshot == null || snapshot.Status == UsageStatus.Loading)
        {
            return palette.SecondaryAccent;
        }
        if (snapshot.IsStale)
        {
            return palette.Warning;
        }
        if (snapshot.Status != UsageStatus.Live)
        {
            return palette.Error;
        }
        if (healthLabel == "额度紧张")
        {
            return palette.Error;
        }
        if (healthLabel == "接近阈值")
        {
            return palette.Warning;
        }
        return palette.Success;
    }

    private Color GetInsightColor(UsageInsight insight)
    {
        if (insight == null || !insight.HasRate)
        {
            return palette.SecondaryText;
        }
        if (insight.Direction == UsageTrendDirection.Rising)
        {
            return palette.Warning;
        }
        if (insight.Direction == UsageTrendDirection.Falling)
        {
            return palette.Success;
        }
        return palette.SecondaryAccent;
    }

    private static string FormatForecast(DateTimeOffset? projectedAt)
    {
        if (!projectedAt.HasValue)
        {
            return "暂无预测";
        }
        TimeSpan remaining = projectedAt.Value - DateTimeOffset.UtcNow;
        if (remaining.TotalMinutes <= 0d)
        {
            return "即将达到";
        }
        if (remaining.TotalDays >= 1d)
        {
            return "约 " + Math.Max(1, (int)Math.Floor(remaining.TotalDays)).ToString(CultureInfo.InvariantCulture) + " 天后用尽";
        }
        if (remaining.TotalHours >= 1d)
        {
            return "约 " + Math.Max(1, (int)Math.Floor(remaining.TotalHours)).ToString(CultureInfo.InvariantCulture) + " 小时后用尽";
        }
        return "约 " + Math.Max(1, (int)Math.Floor(remaining.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " 分钟后用尽";
    }

    private static double Step(double current, double target)
    {
        double delta = target - current;
        double distance = Math.Abs(delta);
        if (distance <= 0.05d)
        {
            return target;
        }
        return current + Math.Sign(delta) * Math.Min(distance, Math.Max(0.8d, distance * 0.18d));
    }

    private Color GetUsageColor(QuotaWindow window, Color accent)
    {
        if (window == null)
        {
            return palette.SecondaryText;
        }
        if (window.UsedPercent >= 95d)
        {
            return palette.Error;
        }
        if (window.UsedPercent >= 80d)
        {
            return palette.Warning;
        }
        return accent;
    }

    private static string GetStatusText(UsageSnapshot value)
    {
        if (value == null || value.Status == UsageStatus.Loading)
        {
            return "读取中";
        }
        if (value.IsStale)
        {
            return "缓存数据";
        }
        switch (value.Status)
        {
            case UsageStatus.Live:
                return "实时 · 正常";
            case UsageStatus.OAuthExpired:
                return "需要登录";
            case UsageStatus.NetworkError:
                return "网络不可用";
            case UsageStatus.ApiError:
                return "官方接口异常";
            case UsageStatus.ParseError:
                return "响应无法解析";
            default:
                return "查询失败";
        }
    }

    private Color GetStatusColor(UsageSnapshot value)
    {
        if (value == null || value.Status == UsageStatus.Loading)
        {
            return palette.SecondaryAccent;
        }
        if (value.IsStale)
        {
            return palette.Warning;
        }
        return value.Status == UsageStatus.Live ? palette.Success : palette.Error;
    }

    private static string FormatReset(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue)
        {
            return "--";
        }
        if ((resetAt.Value - DateTimeOffset.UtcNow).TotalSeconds <= 0)
        {
            return "现在";
        }
        return resetAt.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatCountdown(DateTimeOffset? resetAt)
    {
        if (!resetAt.HasValue)
        {
            return "等待官方重置时间";
        }
        TimeSpan remaining = resetAt.Value - DateTimeOffset.UtcNow;
        if (remaining.TotalSeconds <= 0)
        {
            return "已到重置时间";
        }
        if (remaining.TotalDays >= 1d)
        {
            return "约 " + ((int)Math.Floor(remaining.TotalDays)).ToString(CultureInfo.InvariantCulture) + " 天后重置";
        }
        if (remaining.TotalHours >= 1d)
        {
            return "约 " + ((int)Math.Floor(remaining.TotalHours)).ToString(CultureInfo.InvariantCulture) + " 小时后重置";
        }
        return "约 " + Math.Max(1, (int)Math.Floor(remaining.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " 分钟后重置";
    }

    private static string FormatWindowLabel(int seconds)
    {
        if (seconds == 18000)
        {
            return "5h";
        }
        if (seconds == 604800)
        {
            return "7d";
        }
        if (seconds >= 86400)
        {
            return (seconds / 86400).ToString(CultureInfo.InvariantCulture) + "d";
        }
        if (seconds >= 3600)
        {
            return (seconds / 3600).ToString(CultureInfo.InvariantCulture) + "h";
        }
        return "win";
    }

    private int GetEntranceAlpha()
    {
        return Math.Max(55, Math.Min(255, 80 + (int)Math.Round(175d * Math.Min(1f, entrance))));
    }

    private static void DrawText(Graphics g, string text, string family, float size, FontStyle style, Color color, float x, float y)
    {
        using (Font font = new Font(family, size, style, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(color))
        {
            g.DrawString(text ?? string.Empty, font, brush, x, y);
        }
    }

    private static void DrawTextCentered(Graphics g, string text, string family, float size, FontStyle style, Color color, float centerX, float y)
    {
        using (Font font = new Font(family, size, style, GraphicsUnit.Point))
        using (SolidBrush brush = new SolidBrush(color))
        {
            SizeF measured = g.MeasureString(text ?? string.Empty, font);
            g.DrawString(text ?? string.Empty, font, brush, centerX - measured.Width / 2f, y);
        }
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(2, radius * 2);
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
