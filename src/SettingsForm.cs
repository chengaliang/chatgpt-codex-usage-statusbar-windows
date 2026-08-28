using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// 编辑状态栏的本地偏好。窗口不读取凭据、不显示额度响应，也不保存任何敏感字段。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings draft;
    private readonly Action<int> opacityPreview;
    private readonly ComboBox refreshCombo;
    private readonly ComboBox historyRetentionCombo;
    private readonly TrackBar opacityTrackBar;
    private readonly Label opacityValueLabel;
    private readonly CheckBox clickThroughCheck;
    private readonly ComboBox themeCombo;
    private readonly CheckBox autoStartCheck;
    private readonly ComboBox launchDelayCombo;
    private readonly CheckBox autoCheckUpdatesCheck;
    private readonly CheckBox notificationsCheck;
    private readonly CheckBox restorePositionCheck;
    private readonly CheckBox animationsCheck;
    private readonly ComboBox hotkeyCombo;
    private readonly CheckBox resetNotificationsCheck;
    private readonly CheckBox forecastNotificationsCheck;
    private readonly NumericUpDown thresholdInput;
    private readonly ThemePalette palette;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    public AppSettings Result { get; private set; }

    public SettingsForm(AppSettings settings)
        : this(settings, null)
    {
    }

    /// <summary>
    /// 构造设置窗口。可选的透明度回调只用于预览当前窗口外观，不会在点击保存前改写设置模型。
    /// </summary>
    public SettingsForm(AppSettings settings, Action<int> opacityPreview)
    {
        draft = settings == null ? AppSettings.CreateDefault() : settings.Clone();
        draft.Normalize();
        this.opacityPreview = opacityPreview;
        palette = ThemePalette.Create(draft.Theme);

        Text = "状态栏设置";
        ClientSize = new Size(520, 690);
        MinimumSize = new Size(500, 690);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        // 状态栏是工具窗，居中到父窗体会让设置面板贴在屏幕边缘；独立居中更稳定。
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        BackColor = palette.BackgroundTop;
        ForeColor = palette.PrimaryText;

        TableLayoutPanel layout = new TableLayoutPanel();
        layout.Dock = DockStyle.Fill;
        layout.Padding = new Padding(22, 18, 22, 16);
        layout.ColumnCount = 2;
        layout.RowCount = 17;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54f));
        for (int row = 1; row <= 15; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36f));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44f));

        Panel header = new Panel();
        header.Dock = DockStyle.Fill;
        header.BackColor = Color.Transparent;
        Label title = new Label();
        title.Text = "状态栏设置";
        title.AutoSize = true;
        title.Font = new Font(UiTheme.CjkFontFamily, 16f, FontStyle.Bold);
        title.ForeColor = palette.PrimaryText;
        title.Location = new Point(0, 0);
        Label hint = new Label();
        hint.Text = "调整外观、刷新节奏和隐私友好的本地体验";
        hint.AutoSize = true;
        hint.Font = new Font(UiTheme.CjkFontFamily, 8.5f, FontStyle.Regular);
        hint.ForeColor = palette.SecondaryText;
        hint.Location = new Point(2, 29);
        header.Controls.Add(title);
        header.Controls.Add(hint);
        header.MouseDown += BeginWindowDrag;
        title.MouseDown += BeginWindowDrag;
        hint.MouseDown += BeginWindowDrag;
        layout.Controls.Add(header, 0, 0);
        layout.SetColumnSpan(header, 2);

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

        Panel opacityPanel = new Panel();
        opacityPanel.Dock = DockStyle.Fill;
        opacityPanel.Height = 32;
        opacityPanel.BackColor = Color.Transparent;

        opacityTrackBar = new TrackBar();
        opacityTrackBar.Minimum = AppSettings.MinimumOpacityPercent;
        opacityTrackBar.Maximum = AppSettings.MaximumOpacityPercent;
        opacityTrackBar.TickFrequency = 5;
        opacityTrackBar.LargeChange = 5;
        opacityTrackBar.SmallChange = 1;
        opacityTrackBar.Value = Math.Max(
            AppSettings.MinimumOpacityPercent,
            Math.Min(AppSettings.MaximumOpacityPercent, draft.OpacityPercent));
        opacityTrackBar.AutoSize = false;
        opacityTrackBar.Height = 32;
        opacityTrackBar.Dock = DockStyle.Fill;
        opacityTrackBar.AccessibleName = "背景不透明度滑块";
        opacityTrackBar.ValueChanged += OnOpacityTrackBarValueChanged;

        opacityValueLabel = new Label();
        opacityValueLabel.AutoSize = false;
        opacityValueLabel.Width = 92;
        opacityValueLabel.Dock = DockStyle.Right;
        opacityValueLabel.TextAlign = ContentAlignment.MiddleRight;
        opacityValueLabel.Font = new Font(UiTheme.UiFontFamily, 9f, FontStyle.Bold);
        opacityValueLabel.ForeColor = palette.PrimaryText;
        opacityValueLabel.BackColor = Color.Transparent;
        UpdateOpacityValueLabel();
        opacityPanel.Controls.Add(opacityTrackBar);
        opacityPanel.Controls.Add(opacityValueLabel);

        clickThroughCheck = new CheckBox();
        clickThroughCheck.Text = "忽略鼠标操作（点击穿透）";
        clickThroughCheck.AutoSize = true;
        clickThroughCheck.Checked = draft.ClickThroughEnabled;
        clickThroughCheck.AccessibleName = "忽略鼠标操作并让点击穿透";

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

        animationsCheck = new CheckBox();
        animationsCheck.Text = "平滑动效与状态反馈";
        animationsCheck.AutoSize = true;
        animationsCheck.Checked = draft.AnimationsEnabled;
        animationsCheck.AccessibleName = "开启平滑进度和状态动效";

        hotkeyCombo = CreateCombo();
        hotkeyCombo.Items.Add("Ctrl+Alt+U（唤起 Usage Hub）");
        hotkeyCombo.Items.Add("关闭全局快捷键");
        hotkeyCombo.SelectedIndex = draft.GlobalHotkeyEnabled ? 0 : 1;
        hotkeyCombo.AccessibleName = "全局快捷键设置";

        resetNotificationsCheck = new CheckBox();
        resetNotificationsCheck.Text = "额度周期重置后提醒一次";
        resetNotificationsCheck.AutoSize = true;
        resetNotificationsCheck.Checked = draft.ResetNotificationsEnabled;
        resetNotificationsCheck.AccessibleName = "额度周期重置提醒";

        forecastNotificationsCheck = new CheckBox();
        forecastNotificationsCheck.Text = "预测 2 小时内耗尽时提醒";
        forecastNotificationsCheck.AutoSize = true;
        forecastNotificationsCheck.Checked = draft.ForecastNotificationsEnabled;
        forecastNotificationsCheck.AccessibleName = "额度耗尽预测提醒";

        AddRow(layout, 1, "自动刷新", refreshCombo);
        AddRow(layout, 2, "历史保留", historyRetentionCombo);
        AddRow(layout, 3, "主题", themeCombo);
        AddRow(layout, 4, "背景透明度", opacityPanel);
        AddRow(layout, 5, "鼠标交互", clickThroughCheck);
        AddRow(layout, 6, "开机启动", autoStartCheck);
        AddRow(layout, 7, "启动延迟", launchDelayCombo);
        AddRow(layout, 8, "启动更新检查", autoCheckUpdatesCheck);
        AddRow(layout, 9, "通知", notificationsCheck);
        AddRow(layout, 10, "通知阈值", thresholdInput);
        AddRow(layout, 11, "窗口位置", restorePositionCheck);
        AddRow(layout, 12, "视觉反馈", animationsCheck);
        AddRow(layout, 13, "全局快捷键", hotkeyCombo);
        AddRow(layout, 14, "周期提醒", resetNotificationsCheck);
        AddRow(layout, 15, "预测提醒", forecastNotificationsCheck);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Dock = DockStyle.Fill;
        buttons.WrapContents = false;
        buttons.AutoSize = true;

        Button saveButton = new Button();
        saveButton.Text = "保存";
        saveButton.Width = 96;
        saveButton.Height = 34;
        saveButton.DialogResult = DialogResult.None;
        saveButton.Click += OnSave;

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.Width = 96;
        cancelButton.Height = 34;
        cancelButton.DialogResult = DialogResult.Cancel;

        UiTheme.StyleButton(saveButton, palette, true);
        UiTheme.StyleButton(cancelButton, palette, false);

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 16);
        layout.SetColumnSpan(buttons, 2);
        ApplyControlTheme(layout);
        hint.ForeColor = palette.SecondaryText;
        Controls.Add(layout);

        Button closeButton = new Button();
        closeButton.Text = "×";
        closeButton.Width = 34;
        closeButton.Height = 30;
        closeButton.Location = new Point(ClientSize.Width - 46, 10);
        closeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        closeButton.FlatStyle = FlatStyle.Flat;
        closeButton.FlatAppearance.BorderSize = 0;
        closeButton.BackColor = Color.Transparent;
        closeButton.ForeColor = palette.SecondaryText;
        closeButton.Font = new Font("Segoe UI Symbol", 13f, FontStyle.Regular);
        closeButton.Cursor = Cursors.Hand;
        closeButton.UseVisualStyleBackColor = false;
        closeButton.AccessibleName = "关闭状态栏设置";
        closeButton.Click += delegate(object sender, EventArgs args) { Close(); };
        Controls.Add(closeButton);
        closeButton.BringToFront();
        Paint += delegate(object sender, PaintEventArgs args)
        {
            using (Pen border = new Pen(UiTheme.WithAlpha(palette.ControlBorder, 190), 1f))
            {
                args.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            }
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Shown += delegate(object sender, EventArgs args) { refreshCombo.Focus(); };
        UpdateThresholdState();
    }

    private void BeginWindowDrag(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private static ComboBox CreateCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Font = new Font(UiTheme.CjkFontFamily, 9f, FontStyle.Regular);
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
        label.Font = new Font(UiTheme.CjkFontFamily, 9f, FontStyle.Regular);
        label.Margin = new Padding(0, 0, 8, 0);
        control.Margin = new Padding(0, 2, 0, 2);
        if (string.IsNullOrWhiteSpace(control.AccessibleName))
        {
            control.AccessibleName = labelText;
        }
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
                child.Font = child is CheckBox
                    ? new Font(UiTheme.CjkFontFamily, 9f, FontStyle.Regular)
                    : child.Font;
            }
            else if (child is ComboBox)
            {
                child.ForeColor = palette.PrimaryText;
                child.BackColor = palette.ControlBackground;
                ComboBox combo = (ComboBox)child;
                combo.FlatStyle = FlatStyle.Flat;
                combo.Font = new Font(UiTheme.CjkFontFamily, 9f, FontStyle.Regular);
            }
            else if (child is NumericUpDown)
            {
                child.ForeColor = palette.PrimaryText;
                child.BackColor = palette.ControlBackground;
                child.Font = new Font(UiTheme.UiFontFamily, 9f, FontStyle.Regular);
                ((NumericUpDown)child).BorderStyle = BorderStyle.FixedSingle;
            }
            else if (child is TrackBar)
            {
                child.ForeColor = palette.PrimaryText;
                child.BackColor = palette.BackgroundTop;
            }
            else if (child is Button)
            {
                UiTheme.StyleButton((Button)child, palette, ((Button)child).Text == "保存");
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

    /// <summary>
    /// 更新滑块右侧的数值标签，并把拖动中的值交给状态栏宿主即时预览。
    /// </summary>
    private void OnOpacityTrackBarValueChanged(object sender, EventArgs e)
    {
        UpdateOpacityValueLabel();
        if (opacityPreview != null)
        {
            opacityPreview(opacityTrackBar.Value);
        }
    }

    private void UpdateOpacityValueLabel()
    {
        if (opacityValueLabel != null && opacityTrackBar != null)
        {
            opacityValueLabel.Text = opacityTrackBar.Value + "% 不透明";
        }
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
        draft.OpacityPercent = opacityTrackBar.Value;
        draft.BackgroundStyle = AppSettings.GetBackgroundStyleForOpacity(draft.OpacityPercent);
        draft.ClickThroughEnabled = clickThroughCheck.Checked;
        draft.AutoStartEnabled = autoStartCheck.Checked;
        draft.AutoCheckUpdates = autoCheckUpdatesCheck.Checked;
        draft.NotificationsEnabled = notificationsCheck.Checked;
        draft.NotificationThresholdPercent = Decimal.ToInt32(thresholdInput.Value);
        draft.RestorePosition = restorePositionCheck.Checked;
        draft.AnimationsEnabled = animationsCheck.Checked;
        draft.GlobalHotkeyEnabled = hotkeyCombo.SelectedIndex == 0;
        draft.ResetNotificationsEnabled = resetNotificationsCheck.Checked;
        draft.ForecastNotificationsEnabled = forecastNotificationsCheck.Checked;
        draft.Normalize();
        Result = draft.Clone();
        DialogResult = DialogResult.OK;
        Close();
    }
}
