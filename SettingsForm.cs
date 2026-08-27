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
    private readonly ComboBox backgroundCombo;
    private readonly CheckBox autoStartCheck;
    private readonly CheckBox notificationsCheck;
    private readonly CheckBox restorePositionCheck;
    private readonly NumericUpDown thresholdInput;

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings)
    {
        draft = settings == null ? AppSettings.CreateDefault() : settings.Clone();
        draft.Normalize();

        Text = "状态栏设置";
        ClientSize = new Size(390, 285);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = SystemColors.Window;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(16, 14, 16, 12);
        layout.ColumnCount = 2;
        layout.RowCount = 7;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
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

        backgroundCombo = CreateCombo();
        backgroundCombo.Items.Add("实色");
        backgroundCombo.Items.Add("半透明（85%）");
        backgroundCombo.Items.Add("高透明（65%）");
        backgroundCombo.SelectedIndex = (int)draft.BackgroundStyle;

        autoStartCheck = new CheckBox();
        autoStartCheck.Text = "随 Windows 启动";
        autoStartCheck.AutoSize = true;
        autoStartCheck.Checked = draft.AutoStartEnabled;

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
        AddRow(layout, 1, "背景样式", backgroundCombo);
        AddRow(layout, 2, "开机启动", autoStartCheck);
        AddRow(layout, 3, "通知", notificationsCheck);
        AddRow(layout, 4, "通知阈值", thresholdInput);
        AddRow(layout, 5, "窗口位置", restorePositionCheck);

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
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
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
        draft.BackgroundStyle = (BackgroundStyle)Math.Max(0, backgroundCombo.SelectedIndex);
        draft.AutoStartEnabled = autoStartCheck.Checked;
        draft.NotificationsEnabled = notificationsCheck.Checked;
        draft.NotificationThresholdPercent = Decimal.ToInt32(thresholdInput.Value);
        draft.RestorePosition = restorePositionCheck.Checked;
        draft.Normalize();
        Result = draft.Clone();
        DialogResult = DialogResult.OK;
        Close();
    }
}
