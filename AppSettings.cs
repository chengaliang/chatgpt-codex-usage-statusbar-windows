using System;

/// <summary>
/// 状态栏背景档位。使用有限枚举而不是任意透明度，保证文字和点击区域仍然可用。
/// </summary>
internal enum BackgroundStyle
{
    Opaque = 0,
    SemiTransparent = 1,
    HighTransparency = 2
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
    private static readonly int[] SupportedRefreshIntervals = { 1, 5, 10, 15, 30, 60 };

    public int RefreshIntervalMinutes { get; set; }
    public bool AutoStartEnabled { get; set; }
    public BackgroundStyle BackgroundStyle { get; set; }
    public ThemeMode Theme { get; set; }
    public bool NotificationsEnabled { get; set; }
    public int NotificationThresholdPercent { get; set; }
    public bool RestorePosition { get; set; }
    public bool HasSavedPosition { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }

    public AppSettings()
    {
        RefreshIntervalMinutes = 5;
        AutoStartEnabled = true;
        BackgroundStyle = BackgroundStyle.Opaque;
        Theme = ThemeMode.System;
        NotificationsEnabled = false;
        NotificationThresholdPercent = 80;
        RestorePosition = true;
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

        if (!Enum.IsDefined(typeof(BackgroundStyle), BackgroundStyle))
        {
            BackgroundStyle = BackgroundStyle.Opaque;
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
            AutoStartEnabled = AutoStartEnabled,
            BackgroundStyle = BackgroundStyle,
            Theme = Theme,
            NotificationsEnabled = NotificationsEnabled,
            NotificationThresholdPercent = NotificationThresholdPercent,
            RestorePosition = RestorePosition,
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
}
