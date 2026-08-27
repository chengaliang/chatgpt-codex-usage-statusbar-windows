using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

/// <summary>
/// 展示可操作的安全诊断中心。窗口只接收已经脱敏的诊断快照，不读取凭据或网络响应。
/// </summary>
internal sealed class DiagnosticsForm : Form
{
    private readonly Func<Task<DiagnosticSnapshot>> refreshAction;
    private readonly ThemePalette palette;
    private readonly Label summaryLabel;
    private readonly TableLayoutPanel checksLayout;
    private readonly TextBox reportBox;
    private readonly Button refreshButton;
    private readonly Button copyButton;
    private readonly Button chromeMaximizeButton;
    private DiagnosticSnapshot snapshot;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    public DiagnosticsForm(
        string report,
        IList<DiagnosticCheck> checks,
        Func<Task<DiagnosticSnapshot>> refreshAction,
        ThemeMode theme)
    {
        this.refreshAction = refreshAction;
        palette = ThemePalette.Create(theme);
        snapshot = new DiagnosticSnapshot(report, checks);

        Text = "诊断中心";
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(680, 560);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        // 状态栏本身位于屏幕边缘，使用 CenterParent 会把大窗口偏到角落；诊断工作区始终居中打开。
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = palette.BackgroundTop;
        ForeColor = palette.PrimaryText;
        KeyPreview = true;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(24, 20, 24, 18);
        layout.ColumnCount = 1;
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 308f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40f));

        TableLayoutPanel header = new TableLayoutPanel();
        header.Dock = DockStyle.Fill;
        header.ColumnCount = 2;
        header.RowCount = 2;
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250f));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 27f));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        Label title = new Label();
        title.Text = "诊断中心";
        title.AutoSize = true;
        title.Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold);
        title.ForeColor = palette.PrimaryText;
        title.Anchor = AnchorStyles.Left;
        title.AccessibleName = "诊断中心标题";

        summaryLabel = new Label();
        summaryLabel.AutoSize = false;
        summaryLabel.Dock = DockStyle.Fill;
        summaryLabel.TextAlign = ContentAlignment.MiddleRight;
        summaryLabel.ForeColor = palette.SecondaryText;
        summaryLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        summaryLabel.AccessibleName = "诊断结果摘要";

        Label hint = new Label();
        hint.Text = "以下检查项只提供安全状态和下一步建议，不会展示凭据或完整响应";
        hint.AutoSize = true;
        hint.ForeColor = palette.SecondaryText;
        hint.Anchor = AnchorStyles.Left;

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(summaryLabel, 1, 0);
        header.Controls.Add(hint, 0, 1);
        header.SetColumnSpan(hint, 2);
        header.MouseDown += BeginWindowDrag;
        header.DoubleClick += delegate(object sender, EventArgs args) { ToggleWindowState(); };
        title.MouseDown += BeginWindowDrag;
        title.DoubleClick += delegate(object sender, EventArgs args) { ToggleWindowState(); };
        hint.MouseDown += BeginWindowDrag;
        layout.Controls.Add(header, 0, 0);

        checksLayout = new TableLayoutPanel();
        checksLayout.Dock = DockStyle.Fill;
        checksLayout.AutoScroll = true;
        checksLayout.ColumnCount = 1;
        checksLayout.RowCount = 0;
        checksLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        checksLayout.BackColor = palette.BackgroundTop;
        checksLayout.AccessibleName = "诊断检查项列表";
        layout.Controls.Add(checksLayout, 0, 1);

        reportBox = new TextBox();
        reportBox.Multiline = true;
        reportBox.ReadOnly = true;
        reportBox.ScrollBars = ScrollBars.Both;
        reportBox.WordWrap = false;
        reportBox.Dock = DockStyle.Fill;
        reportBox.Font = new Font("Consolas", 9f);
        reportBox.BackColor = palette.ControlBackground;
        reportBox.ForeColor = palette.PrimaryText;
        reportBox.BorderStyle = BorderStyle.FixedSingle;
        reportBox.AccessibleName = "安全诊断摘要，可复制提交到 Issue";
        layout.Controls.Add(reportBox, 0, 2);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.Dock = DockStyle.Fill;
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.WrapContents = false;

        Button closeButton = new Button();
        closeButton.Text = "关闭";
        closeButton.Width = 78;
        closeButton.Height = 34;
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.AccessibleName = "关闭诊断中心";

        copyButton = new Button();
        copyButton.Text = "复制安全摘要";
        copyButton.Width = 112;
        copyButton.Height = 34;
        copyButton.Click += CopyButtonClick;
        copyButton.AccessibleName = "复制不含凭据的安全诊断摘要";

        refreshButton = new Button();
        refreshButton.Text = "重新检查";
        refreshButton.Width = 94;
        refreshButton.Height = 34;
        refreshButton.Click += RefreshButtonClick;
        refreshButton.AccessibleName = "重新检查诊断项目";

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(copyButton);
        buttons.Controls.Add(refreshButton);
        UiTheme.StyleButton(closeButton, palette, false);
        UiTheme.StyleButton(copyButton, palette, false);
        UiTheme.StyleButton(refreshButton, palette, true);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);

        chromeMaximizeButton = new Button();
        chromeMaximizeButton.Text = "□";
        chromeMaximizeButton.Width = 34;
        chromeMaximizeButton.Height = 30;
        chromeMaximizeButton.Location = new Point(ClientSize.Width - 82, 12);
        chromeMaximizeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        chromeMaximizeButton.FlatStyle = FlatStyle.Flat;
        chromeMaximizeButton.FlatAppearance.BorderSize = 0;
        chromeMaximizeButton.BackColor = Color.Transparent;
        chromeMaximizeButton.ForeColor = palette.SecondaryText;
        chromeMaximizeButton.Font = new Font("Segoe UI Symbol", 11f, FontStyle.Regular);
        chromeMaximizeButton.Cursor = Cursors.Hand;
        chromeMaximizeButton.UseVisualStyleBackColor = false;
        chromeMaximizeButton.AccessibleName = "最大化或还原诊断中心";
        chromeMaximizeButton.Click += delegate(object sender, EventArgs args) { ToggleWindowState(); };
        Controls.Add(chromeMaximizeButton);
        chromeMaximizeButton.BringToFront();

        Button chromeCloseButton = new Button();
        chromeCloseButton.Text = "×";
        chromeCloseButton.Width = 34;
        chromeCloseButton.Height = 30;
        chromeCloseButton.Location = new Point(ClientSize.Width - 46, 12);
        chromeCloseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        chromeCloseButton.FlatStyle = FlatStyle.Flat;
        chromeCloseButton.FlatAppearance.BorderSize = 0;
        chromeCloseButton.BackColor = Color.Transparent;
        chromeCloseButton.ForeColor = palette.SecondaryText;
        chromeCloseButton.Font = new Font("Segoe UI Symbol", 13f, FontStyle.Regular);
        chromeCloseButton.Cursor = Cursors.Hand;
        chromeCloseButton.UseVisualStyleBackColor = false;
        chromeCloseButton.AccessibleName = "关闭诊断中心";
        chromeCloseButton.Click += delegate(object sender, EventArgs args) { Close(); };
        Controls.Add(chromeCloseButton);
        chromeCloseButton.BringToFront();
        Paint += delegate(object sender, PaintEventArgs args)
        {
            using (Pen border = new Pen(UiTheme.WithAlpha(palette.ControlBorder, 190), 1f))
            {
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        };
        CancelButton = closeButton;
        KeyDown += DiagnosticsFormKeyDown;
        Resize += delegate(object sender, EventArgs args)
        {
            chromeMaximizeButton.Text = WindowState == FormWindowState.Maximized ? "❐" : "□";
        };
        Shown += delegate(object sender, EventArgs args) { UpdateView(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // 工具窗口不进入 Alt+Tab；移动和最大化由自定义标题栏按钮处理。
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
    }

    private void BeginWindowDrag(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void ToggleWindowState()
    {
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            chromeMaximizeButton.Text = "□";
            return;
        }

        Screen screen = Screen.FromControl(this);
        if (screen != null)
        {
            MaximizedBounds = screen.WorkingArea;
        }
        WindowState = FormWindowState.Maximized;
        chromeMaximizeButton.Text = "❐";
    }

    public void UpdateSnapshot(DiagnosticSnapshot value)
    {
        snapshot = value ?? new DiagnosticSnapshot(string.Empty, new List<DiagnosticCheck>());
        UpdateView();
    }

    private async void RefreshButtonClick(object sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (refreshAction == null || !refreshButton.Enabled)
        {
            return;
        }

        refreshButton.Enabled = false;
        copyButton.Enabled = false;
        refreshButton.Text = "检查中…";
        try
        {
            DiagnosticSnapshot refreshed = await refreshAction();
            if (refreshed != null && !IsDisposed)
            {
                snapshot = refreshed;
                UpdateView();
            }
        }
        catch (Exception)
        {
            if (!IsDisposed)
            {
                summaryLabel.Text = "检查失败，请稍后重试";
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                refreshButton.Enabled = true;
                copyButton.Enabled = true;
                refreshButton.Text = "重新检查";
            }
        }
    }

    private void CopyButtonClick(object sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(snapshot == null ? string.Empty : snapshot.Report);
            summaryLabel.Text = "已复制安全摘要";
        }
        catch (Exception)
        {
            summaryLabel.Text = "剪贴板不可用，请直接选择摘要文本";
        }
    }

    private async void DiagnosticsFormKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.R)
        {
            e.SuppressKeyPress = true;
            await RefreshAsync();
        }
    }

    private void UpdateView()
    {
        DiagnosticSnapshot safeSnapshot = snapshot ?? new DiagnosticSnapshot(string.Empty, new List<DiagnosticCheck>());
        reportBox.Text = safeSnapshot.Report ?? string.Empty;

        int passCount = 0;
        int warningCount = 0;
        int failCount = 0;
        if (safeSnapshot.Checks != null)
        {
            foreach (DiagnosticCheck check in safeSnapshot.Checks)
            {
                if (check == null)
                {
                    continue;
                }
                if (check.Status == DiagnosticCheckStatus.Pass)
                {
                    passCount++;
                }
                else if (check.Status == DiagnosticCheckStatus.Fail)
                {
                    failCount++;
                }
                else
                {
                    warningCount++;
                }
            }
        }
        summaryLabel.Text = "检查项 " + (passCount + warningCount + failCount) +
            " · 通过 " + passCount + " · 注意 " + warningCount + " · 失败 " + failCount;

        checksLayout.SuspendLayout();
        while (checksLayout.Controls.Count > 0)
        {
            Control oldControl = checksLayout.Controls[0];
            checksLayout.Controls.RemoveAt(0);
            oldControl.Dispose();
        }
        checksLayout.RowStyles.Clear();
        IList<DiagnosticCheck> checks = safeSnapshot.Checks ?? new List<DiagnosticCheck>();
        checksLayout.RowCount = checks.Count == 0 ? 1 : checks.Count;
        if (checks.Count == 0)
        {
            checksLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            checksLayout.Controls.Add(CreateEmptyCheckRow(), 0, 0);
        }
        else
        {
            for (int index = 0; index < checks.Count; index++)
            {
                checksLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
                checksLayout.Controls.Add(CreateCheckRow(checks[index]), 0, index);
            }
        }
        checksLayout.ResumeLayout();
    }

    private Control CreateEmptyCheckRow()
    {
        Label empty = new Label();
        empty.Text = "暂无诊断检查项";
        empty.AutoSize = false;
        empty.Dock = DockStyle.Fill;
        empty.TextAlign = ContentAlignment.MiddleLeft;
        empty.ForeColor = palette.SecondaryText;
        return empty;
    }

    private Control CreateCheckRow(DiagnosticCheck check)
    {
        DiagnosticCheck safeCheck = check ?? new DiagnosticCheck("检查", DiagnosticCheckStatus.Warning, "暂无状态", "稍后重试");
        Panel row = new Panel();
        row.Dock = DockStyle.Fill;
        row.BackColor = palette.Surface;
        row.Padding = new Padding(12, 5, 12, 5);
        row.Margin = new Padding(0, 0, 0, 2);
        row.Paint += delegate(object sender, PaintEventArgs args)
        {
            using (Pen border = new Pen(UiTheme.WithAlpha(palette.ControlBorder, 150), 1f))
            {
                args.Graphics.DrawRectangle(border, 0, 0, row.Width - 1, row.Height - 1);
            }
        };

        Label status = new Label();
        status.Text = GetStatusText(safeCheck.Status);
        status.AutoSize = false;
        status.Width = 62;
        status.Dock = DockStyle.Left;
        status.TextAlign = ContentAlignment.MiddleCenter;
        status.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        status.ForeColor = GetStatusColor(safeCheck.Status);
        status.BackColor = UiTheme.WithAlpha(GetStatusColor(safeCheck.Status), 26);

        Label action = new Label();
        action.Text = safeCheck.NextAction;
        action.AutoSize = false;
        action.Width = 250;
        action.Dock = DockStyle.Right;
        action.TextAlign = ContentAlignment.MiddleRight;
        action.ForeColor = palette.SecondaryText;
        action.AutoEllipsis = true;

        Label detail = new Label();
        detail.Text = safeCheck.Name + "：" + safeCheck.Detail;
        detail.AutoSize = false;
        detail.Dock = DockStyle.Fill;
        detail.TextAlign = ContentAlignment.MiddleLeft;
        detail.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        detail.ForeColor = palette.PrimaryText;
        detail.AutoEllipsis = true;

        row.Controls.Add(detail);
        row.Controls.Add(action);
        row.Controls.Add(status);
        row.AccessibleName = safeCheck.Name + "，" + GetStatusText(safeCheck.Status) + "，" + safeCheck.Detail;
        return row;
    }

    private Color GetStatusColor(DiagnosticCheckStatus status)
    {
        if (status == DiagnosticCheckStatus.Pass)
        {
            return palette.Success;
        }
        return status == DiagnosticCheckStatus.Fail ? palette.Error : palette.Warning;
    }

    private static string GetStatusText(DiagnosticCheckStatus status)
    {
        switch (status)
        {
            case DiagnosticCheckStatus.Pass:
                return "通过";
            case DiagnosticCheckStatus.Fail:
                return "失败";
            default:
                return "注意";
        }
    }
}
