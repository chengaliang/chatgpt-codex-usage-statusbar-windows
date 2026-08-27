using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;

/// <summary>
/// 展示额度快照和本地趋势的详情窗口。窗口只接收已经脱敏的 UsageSnapshot/HistoryPoint，
/// 不读取 OAuth 文件，也不允许把原始接口响应带到控件层。
/// </summary>
internal sealed class UsageDetailsForm : Form
{
    private readonly Func<Task<UsageSnapshot>> refreshAction;
    private readonly Func<IList<HistoryPoint>> historyLoader;
    private readonly Label titleLabel;
    private readonly ThemePalette palette;
    private readonly Label statusLabel;
    private readonly Label lastLiveLabel;
    private readonly TableLayoutPanel windowsLayout;
    private readonly UsageHistoryGraph historyGraph;
    private readonly Button refreshButton;
    private UsageSnapshot snapshot;
    private IList<HistoryPoint> history;

    public UsageDetailsForm(
        UsageSnapshot initialSnapshot,
        IList<HistoryPoint> initialHistory,
        Func<Task<UsageSnapshot>> refreshAction,
        Func<IList<HistoryPoint>> historyLoader)
        : this(initialSnapshot, initialHistory, refreshAction, historyLoader, ThemeMode.System)
    {
    }

    public UsageDetailsForm(
        UsageSnapshot initialSnapshot,
        IList<HistoryPoint> initialHistory,
        Func<Task<UsageSnapshot>> refreshAction,
        Func<IList<HistoryPoint>> historyLoader,
        ThemeMode theme)
    {
        this.refreshAction = refreshAction;
        this.historyLoader = historyLoader;
        palette = ThemePalette.Create(theme);
        snapshot = initialSnapshot == null ? UsageSnapshot.Loading("chatgpt-codex") : initialSnapshot.Clone();
        history = initialHistory == null ? new List<HistoryPoint>() : new List<HistoryPoint>(initialHistory);

        Text = "额度详情";
        ClientSize = new Size(470, 430);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = palette.BackgroundTop;
        ForeColor = palette.PrimaryText;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(18, 16, 18, 14);
        layout.ColumnCount = 2;
        layout.RowCount = 5;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 116f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));

        titleLabel = new Label();
        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold);
        titleLabel.Text = "ChatGPT / Codex";
        titleLabel.ForeColor = palette.PrimaryText;
        titleLabel.Anchor = AnchorStyles.Left;

        statusLabel = new Label();
        statusLabel.AutoSize = true;
        statusLabel.TextAlign = ContentAlignment.MiddleRight;
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

        layout.Controls.Add(titleLabel, 0, 0);
        layout.Controls.Add(statusLabel, 1, 0);

        windowsLayout = new TableLayoutPanel();
        windowsLayout.Dock = DockStyle.Fill;
        windowsLayout.AutoScroll = true;
        windowsLayout.BackColor = palette.BackgroundTop;
        windowsLayout.ColumnCount = 1;
        windowsLayout.RowCount = 0;
        windowsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.Controls.Add(windowsLayout, 0, 1);
        layout.SetColumnSpan(windowsLayout, 2);

        lastLiveLabel = new Label();
        lastLiveLabel.AutoSize = false;
        lastLiveLabel.Dock = DockStyle.Fill;
        lastLiveLabel.TextAlign = ContentAlignment.MiddleLeft;
        lastLiveLabel.ForeColor = palette.SecondaryText;
        layout.Controls.Add(lastLiveLabel, 0, 2);
        layout.SetColumnSpan(lastLiveLabel, 2);

        historyGraph = new UsageHistoryGraph();
        historyGraph.Dock = DockStyle.Fill;
        historyGraph.ApplyPalette(palette);
        historyGraph.Margin = new Padding(0, 3, 0, 5);
        layout.Controls.Add(historyGraph, 0, 3);
        layout.SetColumnSpan(historyGraph, 2);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Dock = DockStyle.Fill;
        buttons.WrapContents = false;

        Button closeButton = new Button();
        closeButton.Text = "关闭";
        closeButton.Width = 78;
        closeButton.Height = 28;
        closeButton.DialogResult = DialogResult.Cancel;

        refreshButton = new Button();
        refreshButton.Text = "立即刷新";
        refreshButton.Width = 88;
        refreshButton.Height = 28;
        refreshButton.Click += RefreshButtonClick;

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(refreshButton);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = refreshButton;
        CancelButton = closeButton;
        Shown += delegate(object sender, EventArgs args) { UpdateView(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
    }

    private async void RefreshButtonClick(object sender, EventArgs e)
    {
        if (refreshAction == null || refreshButton.Enabled == false)
        {
            return;
        }

        refreshButton.Enabled = false;
        refreshButton.Text = "刷新中…";
        try
        {
            UsageSnapshot refreshed = await refreshAction();
            if (refreshed != null)
            {
                snapshot = refreshed.Clone();
            }
            if (historyLoader != null)
            {
                IList<HistoryPoint> refreshedHistory = historyLoader();
                history = refreshedHistory == null ? new List<HistoryPoint>() : new List<HistoryPoint>(refreshedHistory);
            }
            UsageDetailsForm owner = this;
            if (!owner.IsDisposed)
            {
                UpdateView();
            }
        }
        catch (Exception)
        {
            // 主窗口已经提供统一的失败状态，详情窗口只恢复按钮，避免泄露异常原文。
        }
        finally
        {
            if (!IsDisposed)
            {
                refreshButton.Enabled = true;
                refreshButton.Text = "立即刷新";
            }
        }
    }

    public void UpdateSnapshot(UsageSnapshot value, IList<HistoryPoint> points)
    {
        snapshot = value == null ? UsageSnapshot.Loading("chatgpt-codex") : value.Clone();
        history = points == null ? new List<HistoryPoint>() : new List<HistoryPoint>(points);
        UpdateView();
    }

    private void UpdateView()
    {
        titleLabel.Text = string.IsNullOrWhiteSpace(snapshot.PlanName)
            ? "ChatGPT / Codex"
            : "ChatGPT / Codex · " + snapshot.PlanName;
        statusLabel.Text = GetStatusText(snapshot);
        statusLabel.ForeColor = snapshot.Status == UsageStatus.Live && !snapshot.IsStale
            ? palette.Success
            : palette.Warning;

        windowsLayout.SuspendLayout();
        while (windowsLayout.Controls.Count > 0)
        {
            Control oldControl = windowsLayout.Controls[0];
            windowsLayout.Controls.RemoveAt(0);
            oldControl.Dispose();
        }
        windowsLayout.RowStyles.Clear();
        if (snapshot.Windows == null || snapshot.Windows.Count == 0)
        {
            Label empty = CreateWindowLabel("暂无可展示的额度窗口", palette.SecondaryText);
            windowsLayout.RowCount = 1;
            windowsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
            windowsLayout.Controls.Add(empty, 0, 0);
        }
        else
        {
            windowsLayout.RowCount = snapshot.Windows.Count;
            for (int index = 0; index < snapshot.Windows.Count; index++)
            {
                UsageWindow window = snapshot.Windows[index];
                windowsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
                windowsLayout.Controls.Add(CreateWindowPanel(window), 0, index);
            }
        }
        windowsLayout.ResumeLayout();

        if (snapshot.LastLiveAt.HasValue)
        {
            string prefix = snapshot.IsStale ? "最近成功（缓存）" : "最近成功";
            lastLiveLabel.Text = prefix + "：" + snapshot.LastLiveAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        else
        {
            lastLiveLabel.Text = "最近成功：暂无";
        }

        historyGraph.Points = history;
        historyGraph.Invalidate();
    }

    private static Label CreateWindowLabel(string text, Color color)
    {
        Label label = new Label();
        label.Text = text;
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = color;
        return label;
    }

    private Control CreateWindowPanel(UsageWindow window)
    {
        Panel panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = palette.Surface;
        panel.Padding = new Padding(10, 5, 10, 4);

        Label name = new Label();
        name.Text = window.DisplayName;
        name.AutoSize = true;
        name.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        name.ForeColor = palette.PrimaryText;
        name.Location = new Point(10, 5);

        Label percent = new Label();
        percent.Text = window.UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "% 已使用";
        percent.AutoSize = true;
        percent.ForeColor = palette.PrimaryText;
        percent.Location = new Point(10, 25);

        Label reset = new Label();
        reset.Text = "重置 " + FormatReset(window.ResetAt);
        reset.AutoSize = true;
        reset.ForeColor = palette.SecondaryText;
        reset.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        reset.Location = new Point(290, 14);

        panel.Controls.Add(name);
        panel.Controls.Add(percent);
        panel.Controls.Add(reset);
        return panel;
    }

    private static string GetStatusText(UsageSnapshot value)
    {
        if (value == null)
        {
            return "未查询";
        }
        if (value.IsStale)
        {
            if (value.Status == UsageStatus.Cached)
            {
                return "缓存";
            }
            return "缓存 · " + GetFailureText(value.Status);
        }
        switch (value.Status)
        {
            case UsageStatus.Live:
                return "在线 · 正常";
            case UsageStatus.OAuthExpired:
                return "OAuth 不可用";
            case UsageStatus.NetworkError:
                return "网络不可用";
            case UsageStatus.ApiError:
                return "官方接口异常";
            case UsageStatus.ParseError:
                return "响应无法解析";
            case UsageStatus.Loading:
                return "读取中";
            default:
                return "查询失败";
        }
    }

    private static string GetFailureText(UsageStatus status)
    {
        switch (status)
        {
            case UsageStatus.OAuthExpired:
                return "OAuth 不可用";
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
        return resetAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// 使用 GDI+ 绘制最近本地历史。只画百分比，不画账户标签或接口原文，数据不足时显示明确空状态。
/// </summary>
internal sealed class UsageHistoryGraph : Control
{
    private IList<HistoryPoint> points = new List<HistoryPoint>();
    private ThemePalette palette = ThemePalette.Create(ThemeMode.Light);

    public IList<HistoryPoint> Points
    {
        get { return points; }
        set { points = value ?? new List<HistoryPoint>(); }
    }

    public UsageHistoryGraph()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = palette.Surface;
        ForeColor = Color.FromArgb(83, 96, 112);
    }

    public void ApplyPalette(ThemePalette value)
    {
        palette = value ?? ThemePalette.Create(ThemeMode.Light);
        BackColor = palette.Surface;
        ForeColor = palette.SecondaryText;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle plot = new Rectangle(34, 12, Math.Max(10, Width - 50), Math.Max(10, Height - 28));
        using (Pen border = new Pen(palette.ControlBorder))
        using (Pen grid = new Pen(palette.Grid))
        using (Font font = new Font("Consolas", 7f))
        using (SolidBrush labelBrush = new SolidBrush(ForeColor))
        {
            g.DrawRectangle(border, plot);
            for (int step = 0; step <= 4; step++)
            {
                float y = plot.Top + plot.Height * step / 4f;
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                string text = (100 - step * 25).ToString(CultureInfo.InvariantCulture);
                g.DrawString(text, font, labelBrush, 4, y - 6);
            }
        }

        if (points == null || points.Count == 0)
        {
            using (Font emptyFont = new Font("Microsoft YaHei UI", 8.5f))
            using (SolidBrush emptyBrush = new SolidBrush(palette.SecondaryText))
            {
                g.DrawString("至少两次成功刷新后，这里会显示本地趋势", emptyFont, emptyBrush, plot.Left + 12, plot.Top + plot.Height / 2 - 8);
            }
            return;
        }

        List<int> windowSeconds = GetSeriesWindows();
        Color[] colors = { palette.PrimaryAccent, palette.SecondaryAccent, palette.Success };
        bool hasTrend = false;
        for (int index = 0; index < windowSeconds.Count && index < colors.Length; index++)
        {
            hasTrend = DrawSeries(
                g,
                plot,
                windowSeconds[index],
                colors[index],
                FormatWindowLabel(windowSeconds[index]),
                index) || hasTrend;
        }
        if (!hasTrend)
        {
            using (Font emptyFont = new Font("Microsoft YaHei UI", 8.5f))
            using (SolidBrush emptyBrush = new SolidBrush(palette.SecondaryText))
            {
                g.DrawString("正在收集数据（至少两次成功刷新后显示趋势）", emptyFont, emptyBrush, plot.Left + 12, plot.Top + plot.Height / 2 - 8);
            }
        }
    }

    private List<int> GetSeriesWindows()
    {
        HashSet<int> available = new HashSet<int>();
        foreach (HistoryPoint point in points)
        {
            if (point != null && point.LimitWindowSeconds > 0)
            {
                available.Add(point.LimitWindowSeconds);
            }
        }

        List<int> ordered = new List<int>();
        AddPreferredWindow(available, ordered, 86400);
        AddPreferredWindow(available, ordered, 604800);
        List<int> remaining = new List<int>(available);
        remaining.Sort();
        foreach (int seconds in remaining)
        {
            if (!ordered.Contains(seconds))
            {
                ordered.Add(seconds);
            }
        }
        return ordered;
    }

    private static void AddPreferredWindow(HashSet<int> available, IList<int> ordered, int seconds)
    {
        if (available.Contains(seconds))
        {
            ordered.Add(seconds);
        }
    }

    private static string FormatWindowLabel(int seconds)
    {
        if (seconds == 18000)
        {
            return "5h";
        }
        if (seconds == 86400)
        {
            return "24h";
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
        if (seconds >= 60)
        {
            return (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m";
        }
        return "win";
    }

    private bool DrawSeries(Graphics g, Rectangle plot, int seconds, Color color, string name, int legendIndex)
    {
        List<HistoryPoint> series = new List<HistoryPoint>();
        foreach (HistoryPoint point in points)
        {
            if (point != null && point.LimitWindowSeconds == seconds)
            {
                series.Add(point);
            }
        }
        if (series.Count == 0)
        {
            return false;
        }
        series.Sort(delegate(HistoryPoint left, HistoryPoint right) { return left.ObservedAt.CompareTo(right.ObservedAt); });
        if (series.Count > 50)
        {
            series = series.GetRange(series.Count - 50, 50);
        }
        if (series.Count < 2)
        {
            return false;
        }

        PointF[] line = new PointF[series.Count];
        for (int index = 0; index < series.Count; index++)
        {
            float x = series.Count == 1
                ? plot.Left + plot.Width / 2f
                : plot.Left + plot.Width * index / (float)(series.Count - 1);
            float y = plot.Bottom - plot.Height * (float)series[index].UsedPercent / 100f;
            line[index] = new PointF(x, y);
        }
        using (Pen pen = new Pen(color, 2f))
        using (SolidBrush brush = new SolidBrush(color))
        using (Font legendFont = new Font("Consolas", 7f, FontStyle.Bold))
        {
            if (line.Length > 1)
            {
                g.DrawLines(pen, line);
            }
            foreach (PointF point in line)
            {
                g.FillEllipse(brush, point.X - 2.5f, point.Y - 2.5f, 5f, 5f);
            }
            float legendX = plot.Right - 36 - legendIndex * 46;
            g.DrawString(name, legendFont, brush, legendX, 1);
        }
        return true;
    }
}
