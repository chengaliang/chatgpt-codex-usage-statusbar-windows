using System;

/// <summary>
/// 状态栏背景档位。保留预设值用于兼容旧版本和右键菜单，精确透明度由 AppSettings.OpacityPercent 承载。
/// </summary>
internal enum BackgroundStyle
{
    Opaque = 0,
    SemiTransparent = 1,
    HighTransparency = 2,
    UltraTransparency = 3,
    Custom = 4
}

/// <summary>
/// 状态栏配色主题。主题只影响本地绘制，不改变 Provider、缓存或网络行为。
/// </summary>
internal enum ThemeMode
{
    System = 0,
    Dark = 1,
    Light = 2,
    Graphite = 3
}

/// <summary>
/// 当前用户的非敏感偏好。该模型只保存界面和调度设置，不允许承载 OAuth 或接口响应。
/// </summary>
internal sealed class AppSettings
{
    public const int MinimumOpacityPercent = 35;
    public const int MaximumOpacityPercent = 100;
    private static readonly int[] SupportedRefreshIntervals = { 1, 5, 10, 15, 30, 60 };
    private static readonly int[] SupportedHistoryRetentionDays = { 7, 30, 90 };
    private static readonly int[] SupportedLaunchDelaySeconds = { 0, 5, 15, 30 };

    public int RefreshIntervalMinutes { get; set; }
    public int HistoryRetentionDays { get; set; }
    public bool AutoStartEnabled { get; set; }
    public int LaunchDelaySeconds { get; set; }
    public bool AutoCheckUpdates { get; set; }
    public BackgroundStyle BackgroundStyle { get; set; }
    /// <summary>
    /// 状态栏背景的不透明度百分比，范围 35-100；数值越低，窗口越透明。
    /// </summary>
    public int OpacityPercent { get; set; }
    public bool ClickThroughEnabled { get; set; }
    public ThemeMode Theme { get; set; }
    public bool NotificationsEnabled { get; set; }
    public int NotificationThresholdPercent { get; set; }
    public bool RestorePosition { get; set; }
    public bool AnimationsEnabled { get; set; }
    public bool GlobalHotkeyEnabled { get; set; }
    public bool ResetNotificationsEnabled { get; set; }
    public bool ForecastNotificationsEnabled { get; set; }
    public bool HasSavedPosition { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }

    public AppSettings()
    {
        RefreshIntervalMinutes = 5;
        HistoryRetentionDays = 30;
        AutoStartEnabled = true;
        LaunchDelaySeconds = 0;
        AutoCheckUpdates = false;
        BackgroundStyle = BackgroundStyle.Opaque;
        OpacityPercent = MaximumOpacityPercent;
        ClickThroughEnabled = false;
        Theme = ThemeMode.System;
        NotificationsEnabled = false;
        NotificationThresholdPercent = 80;
        RestorePosition = true;
        AnimationsEnabled = true;
        GlobalHotkeyEnabled = true;
        ResetNotificationsEnabled = false;
        ForecastNotificationsEnabled = false;
        HasSavedPosition = false;
        PositionX = 0;
        PositionY = 0;
    }

    public static AppSettings CreateDefault()
    {
        return new AppSettings();
    }

    /// <summary>
    /// 将外部 JSON 或设置窗口输入限制到支持范围，防止非法值破坏计时器和界面布局。
    /// </summary>
    public void Normalize()
    {
        if (!IsSupportedRefreshInterval(RefreshIntervalMinutes))
        {
            RefreshIntervalMinutes = 5;
        }

        if (!IsSupportedHistoryRetentionDays(HistoryRetentionDays))
        {
            HistoryRetentionDays = 30;
        }

        if (!IsSupportedLaunchDelaySeconds(LaunchDelaySeconds))
        {
            LaunchDelaySeconds = 0;
        }

        if (!Enum.IsDefined(typeof(BackgroundStyle), BackgroundStyle))
        {
            BackgroundStyle = BackgroundStyle.Opaque;
        }

        if (!IsSupportedOpacityPercent(OpacityPercent))
        {
            OpacityPercent = GetOpacityForStyle(BackgroundStyle);
        }

        // 滑杆可以落在预设之间；此时使用 Custom 标记，右键预设不会误显示为已选中。
        // Custom 也要按实际百分比重新归类，避免“自定义 100%”在诊断中长期残留。
        if (BackgroundStyle == BackgroundStyle.Custom || GetOpacityForStyle(BackgroundStyle) != OpacityPercent)
        {
            BackgroundStyle = GetBackgroundStyleForOpacity(OpacityPercent);
        }

        if (!Enum.IsDefined(typeof(ThemeMode), Theme))
        {
            Theme = ThemeMode.System;
        }

        if (NotificationThresholdPercent < 50 || NotificationThresholdPercent > 100)
        {
            NotificationThresholdPercent = 80;
        }
    }

    public AppSettings Clone()
    {
        return new AppSettings
        {
            RefreshIntervalMinutes = RefreshIntervalMinutes,
            HistoryRetentionDays = HistoryRetentionDays,
            AutoStartEnabled = AutoStartEnabled,
            LaunchDelaySeconds = LaunchDelaySeconds,
            AutoCheckUpdates = AutoCheckUpdates,
            BackgroundStyle = BackgroundStyle,
            OpacityPercent = OpacityPercent,
            ClickThroughEnabled = ClickThroughEnabled,
            Theme = Theme,
            NotificationsEnabled = NotificationsEnabled,
            NotificationThresholdPercent = NotificationThresholdPercent,
            RestorePosition = RestorePosition,
            AnimationsEnabled = AnimationsEnabled,
            GlobalHotkeyEnabled = GlobalHotkeyEnabled,
            ResetNotificationsEnabled = ResetNotificationsEnabled,
            ForecastNotificationsEnabled = ForecastNotificationsEnabled,
            HasSavedPosition = HasSavedPosition,
            PositionX = PositionX,
            PositionY = PositionY
        };
    }

    public static bool IsSupportedRefreshInterval(int minutes)
    {
        foreach (int supported in SupportedRefreshIntervals)
        {
            if (supported == minutes)
            {
                return true;
            }
        }
        return false;
    }

    public static int[] GetSupportedRefreshIntervals()
    {
        return (int[])SupportedRefreshIntervals.Clone();
    }

    public static bool IsSupportedHistoryRetentionDays(int days)
    {
        foreach (int supported in SupportedHistoryRetentionDays)
        {
            if (supported == days)
            {
                return true;
            }
        }
        return false;
    }

    public static int[] GetSupportedHistoryRetentionDays()
    {
        return (int[])SupportedHistoryRetentionDays.Clone();
    }

    public static bool IsSupportedLaunchDelaySeconds(int seconds)
    {
        foreach (int supported in SupportedLaunchDelaySeconds)
        {
            if (supported == seconds)
            {
                return true;
            }
        }
        return false;
    }

    public static int[] GetSupportedLaunchDelaySeconds()
    {
        return (int[])SupportedLaunchDelaySeconds.Clone();
    }

    public static bool IsSupportedOpacityPercent(int value)
    {
        return value >= MinimumOpacityPercent && value <= MaximumOpacityPercent;
    }

    public static int GetOpacityForStyle(BackgroundStyle style)
    {
        switch (style)
        {
            case BackgroundStyle.SemiTransparent:
                return 85;
            case BackgroundStyle.HighTransparency:
                return 65;
            case BackgroundStyle.UltraTransparency:
                return 35;
            case BackgroundStyle.Custom:
                return MaximumOpacityPercent;
            default:
                return MaximumOpacityPercent;
        }
    }

    public static BackgroundStyle GetBackgroundStyleForOpacity(int opacityPercent)
    {
        switch (opacityPercent)
        {
            case 100:
                return BackgroundStyle.Opaque;
            case 85:
                return BackgroundStyle.SemiTransparent;
            case 65:
                return BackgroundStyle.HighTransparency;
            case 35:
                return BackgroundStyle.UltraTransparency;
            default:
                return BackgroundStyle.Custom;
        }
    }
}
