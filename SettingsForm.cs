using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// 编辑状态栏的本地偏好。窗口不读取凭据、不显示额度响应，也不保存任何敏感字段。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings draft;
    private readonly ComboBox refreshCombo;
    private readonly ComboBox historyRetentionCombo;
    private readonly ComboBox backgroundCombo;
    private readonly ComboBox themeCombo;
    private readonly CheckBox autoStartCheck;
    private readonly ComboBox launchDelayCombo;
    private readonly CheckBox autoCheckUpdatesCheck;
    private readonly CheckBox notificationsCheck;
    private readonly CheckBox restorePositionCheck;
    private readonly NumericUpDown thresholdInput;
    private readonly ThemePalette palette;

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        draft = settings == null ? AppSettings.CreateDefault() : settings.Clone();
        draft.Normalize();
        palette = ThemePalette.Create(draft.Theme);

        Text = "状态栏设置";
        ClientSize = new Size(390, 430);
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
        layout.Padding = new Padding(16, 14, 16, 12);
        layout.ColumnCount = 2;
        layout.RowCount = 11;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        refreshCombo = CreateCombo();
        foreach (int minutes in AppSettings.GetSupportedRefreshIntervals())
        {
            refreshCombo.Items.Add(minutes + " 分钟");
        }
        refreshCombo.SelectedIndex = FindRefreshIndex(draft.RefreshIntervalMinutes);

        historyRetentionCombo = CreateCombo();
        foreach (int days in AppSettings.GetSupportedHistoryRetentionDays())
        {
            historyRetentionCombo.Items.Add(days + " 天");
        }
        historyRetentionCombo.SelectedIndex = FindValueIndex(
            AppSettings.GetSupportedHistoryRetentionDays(),
            draft.HistoryRetentionDays,
            1);

        backgroundCombo = CreateCombo();
        backgroundCombo.Items.Add("实色");
        backgroundCombo.Items.Add("半透明（85%）");
        backgroundCombo.Items.Add("高透明（65%）");
        backgroundCombo.SelectedIndex = (int)draft.BackgroundStyle;

        themeCombo = CreateCombo();
        themeCombo.Items.Add("跟随系统");
        themeCombo.Items.Add("深色");
        themeCombo.Items.Add("浅色");
        themeCombo.Items.Add("石墨");
        themeCombo.SelectedIndex = (int)draft.Theme;

        autoStartCheck = new CheckBox();
        autoStartCheck.Text = "随 Windows 启动";
        autoStartCheck.AutoSize = true;
        autoStartCheck.Checked = draft.AutoStartEnabled;

        launchDelayCombo = CreateCombo();
        launchDelayCombo.Items.Add("立即查询");
        launchDelayCombo.Items.Add("启动后 5 秒");
        launchDelayCombo.Items.Add("启动后 15 秒");
        launchDelayCombo.Items.Add("启动后 30 秒");
        launchDelayCombo.SelectedIndex = FindValueIndex(
            AppSettings.GetSupportedLaunchDelaySeconds(),
            draft.LaunchDelaySeconds,
            0);

        autoCheckUpdatesCheck = new CheckBox();
        autoCheckUpdatesCheck.Text = "启动时检查更新（仅提示）";
        autoCheckUpdatesCheck.AutoSize = true;
        autoCheckUpdatesCheck.Checked = draft.AutoCheckUpdates;

        notificationsCheck = new CheckBox();
        notificationsCheck.Text = "额度接近阈值时通知";
        notificationsCheck.AutoSize = true;
        notificationsCheck.Checked = draft.NotificationsEnabled;
        notificationsCheck.CheckedChanged += OnNotificationsChanged;

        thresholdInput = new NumericUpDown();
        thresholdInput.Minimum = 50;
        thresholdInput.Maximum = 100;
        thresholdInput.Increment = 5;
        thresholdInput.Value = draft.NotificationThresholdPercent;
        thresholdInput.Width = 90;

        restorePositionCheck = new CheckBox();
        restorePositionCheck.Text = "记住上次位置";
        restorePositionCheck.AutoSize = true;
        restorePositionCheck.Checked = draft.RestorePosition;

        AddRow(layout, 0, "自动刷新", refreshCombo);
        AddRow(layout, 1, "历史保留", historyRetentionCombo);
        AddRow(layout, 2, "主题", themeCombo);
        AddRow(layout, 3, "背景样式", backgroundCombo);
        AddRow(layout, 4, "开机启动", autoStartCheck);
        AddRow(layout, 5, "启动延迟", launchDelayCombo);
        AddRow(layout, 6, "启动更新检查", autoCheckUpdatesCheck);
        AddRow(layout, 7, "通知", notificationsCheck);
        AddRow(layout, 8, "通知阈值", thresholdInput);
        AddRow(layout, 9, "窗口位置", restorePositionCheck);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Dock = DockStyle.Fill;
        buttons.WrapContents = false;
        buttons.AutoSize = true;

        Button saveButton = new Button();
        saveButton.Text = "保存";
        saveButton.Width = 82;
        saveButton.Height = 28;
        saveButton.DialogResult = DialogResult.None;
        saveButton.Click += OnSave;

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.Width = 82;
        cancelButton.Height = 28;
        cancelButton.DialogResult = DialogResult.Cancel;

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 10);
        layout.SetColumnSpan(buttons, 2);
        ApplyControlTheme(layout);
        Controls.Add(layout);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += delegate(object sender, EventArgs args) { refreshCombo.Focus(); };
        UpdateThresholdState();
    }

    private static ComboBox CreateCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.MaxDropDownItems = 8;
        return combo;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control control)
    {
        Label label = new Label();
        label.Text = labelText;
        label.AutoSize = true;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 0, 8, 0);
        control.Margin = new Padding(0, 2, 0, 2);
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void ApplyControlTheme(Control root)
    {
        root.BackColor = palette.BackgroundTop;
        foreach (Control child in root.Controls)
        {
            if (child is Label || child is CheckBox)
            {
                child.ForeColor = palette.PrimaryText;
                child.BackColor = Color.Transparent;
            }
            else if (child is ComboBox || child is NumericUpDown || child is Button)
            {
                child.ForeColor = palette.PrimaryText;
                child.BackColor = palette.ControlBackground;
            }
            else
            {
                child.BackColor = palette.BackgroundTop;
            }
            if (child.HasChildren)
            {
                ApplyControlTheme(child);
            }
        }
    }

    private static int FindRefreshIndex(int minutes)
    {
        int[] values = AppSettings.GetSupportedRefreshIntervals();
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] == minutes)
            {
                return index;
            }
        }
        return 1;
    }

    private static int FindValueIndex(int[] values, int selectedValue, int fallbackIndex)
    {
        if (values == null || values.Length == 0)
        {
            return -1;
        }
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] == selectedValue)
            {
                return index;
            }
        }
        return Math.Max(0, Math.Min(values.Length - 1, fallbackIndex));
    }

    private void OnNotificationsChanged(object sender, EventArgs e)
    {
        UpdateThresholdState();
    }

    private void UpdateThresholdState()
    {
        thresholdInput.Enabled = notificationsCheck.Checked;
    }

    private void OnSave(object sender, EventArgs e)
    {
        int[] intervals = AppSettings.GetSupportedRefreshIntervals();
        int selectedIndex = refreshCombo.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= intervals.Length)
        {
            selectedIndex = 1;
        }

        draft.RefreshIntervalMinutes = intervals[selectedIndex];
        int[] retentionValues = AppSettings.GetSupportedHistoryRetentionDays();
        int retentionIndex = historyRetentionCombo.SelectedIndex;
        if (retentionIndex < 0 || retentionIndex >= retentionValues.Length)
        {
            retentionIndex = 1;
        }
        draft.HistoryRetentionDays = retentionValues[retentionIndex];

        int[] delayValues = AppSettings.GetSupportedLaunchDelaySeconds();
        int delayIndex = launchDelayCombo.SelectedIndex;
        if (delayIndex < 0 || delayIndex >= delayValues.Length)
        {
            delayIndex = 0;
        }
        draft.LaunchDelaySeconds = delayValues[delayIndex];
        draft.Theme = (ThemeMode)Math.Max(0, themeCombo.SelectedIndex);
        draft.BackgroundStyle = (BackgroundStyle)Math.Max(0, backgroundCombo.SelectedIndex);
        draft.AutoStartEnabled = autoStartCheck.Checked;
        draft.AutoCheckUpdates = autoCheckUpdatesCheck.Checked;
        draft.NotificationsEnabled = notificationsCheck.Checked;
        draft.NotificationThresholdPercent = Decimal.ToInt32(thresholdInput.Value);
        draft.RestorePosition = restorePositionCheck.Checked;
        draft.Normalize();
        Result = draft.Clone();
        DialogResult = DialogResult.OK;
        Close();
    }
}
