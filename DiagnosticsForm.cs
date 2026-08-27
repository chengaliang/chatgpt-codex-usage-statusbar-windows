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
    private DiagnosticSnapshot snapshot;

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
        ClientSize = new Size(700, 600);
        MinimumSize = new Size(620, 500);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = palette.BackgroundTop;
        ForeColor = palette.PrimaryText;
        KeyPreview = true;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(18, 16, 18, 14);
        layout.ColumnCount = 1;
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 300f));
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
        title.Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold);
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
        hint.Text = "以下检查项只提供安全状态和下一步建议";
        hint.AutoSize = true;
        hint.ForeColor = palette.SecondaryText;
        hint.Anchor = AnchorStyles.Left;

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(summaryLabel, 1, 0);
        header.Controls.Add(hint, 0, 1);
        header.SetColumnSpan(hint, 2);
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
        closeButton.Height = 28;
        closeButton.DialogResult = DialogResult.Cancel;
        closeButton.AccessibleName = "关闭诊断中心";

        copyButton = new Button();
        copyButton.Text = "复制安全摘要";
        copyButton.Width = 112;
        copyButton.Height = 28;
        copyButton.Click += CopyButtonClick;
        copyButton.AccessibleName = "复制不含凭据的安全诊断摘要";

        refreshButton = new Button();
        refreshButton.Text = "重新检查";
        refreshButton.Width = 94;
        refreshButton.Height = 28;
        refreshButton.Click += RefreshButtonClick;
        refreshButton.AccessibleName = "重新检查诊断项目";

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(copyButton);
        buttons.Controls.Add(refreshButton);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
        CancelButton = closeButton;
        KeyDown += DiagnosticsFormKeyDown;
        Shown += delegate(object sender, EventArgs args) { UpdateView(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            // 工具窗口不进入 Alt+Tab，但保留可调整大小的诊断工作区。
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
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
        row.Padding = new Padding(10, 4, 10, 4);

        Label status = new Label();
        status.Text = GetStatusText(safeCheck.Status);
        status.AutoSize = false;
        status.Width = 54;
        status.Dock = DockStyle.Left;
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        status.ForeColor = GetStatusColor(safeCheck.Status);

        Label action = new Label();
        action.Text = safeCheck.NextAction;
        action.AutoSize = false;
        action.Width = 220;
        action.Dock = DockStyle.Right;
        action.TextAlign = ContentAlignment.MiddleRight;
        action.ForeColor = palette.SecondaryText;
        action.AutoEllipsis = true;

        Label detail = new Label();
        detail.Text = safeCheck.Name + "：" + safeCheck.Detail;
        detail.AutoSize = false;
        detail.Dock = DockStyle.Fill;
        detail.TextAlign = ContentAlignment.MiddleLeft;
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
