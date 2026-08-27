using System;
using System.Drawing;
using Microsoft.Win32;

/// <summary>
/// 状态栏绘制所需的有限颜色集合。集中管理主题色可以避免局部控件出现不一致的对比度。
/// </summary>
internal sealed class ThemePalette
{
    public Color BackgroundTop { get; private set; }
    public Color BackgroundBottom { get; private set; }
    public Color Border { get; private set; }
    public Color Divider { get; private set; }
    public Color PrimaryText { get; private set; }
    public Color SecondaryText { get; private set; }
    public Color Track { get; private set; }
    public Color ButtonHover { get; private set; }
    public Color ButtonIcon { get; private set; }
    public Color Success { get; private set; }
    public Color Warning { get; private set; }
    public Color Error { get; private set; }
    public Color PrimaryAccent { get; private set; }
    public Color SecondaryAccent { get; private set; }
    public Color Surface { get; private set; }
    public Color ControlBackground { get; private set; }
    public Color ControlBorder { get; private set; }
    public Color Grid { get; private set; }

    private ThemePalette()
    {
    }

    public static ThemePalette Create(ThemeMode mode)
    {
        if (mode == ThemeMode.System)
        {
            mode = DetectSystemTheme();
        }
        switch (mode)
        {
            case ThemeMode.Light:
                return new ThemePalette
                {
                    BackgroundTop = Color.FromArgb(250, 252, 255),
                    BackgroundBottom = Color.FromArgb(228, 235, 243),
                    Border = Color.FromArgb(182, 194, 207),
                    Divider = Color.FromArgb(205, 214, 224),
                    PrimaryText = Color.FromArgb(28, 38, 49),
                    SecondaryText = Color.FromArgb(82, 98, 116),
                    Track = Color.FromArgb(195, 207, 219),
                    ButtonHover = Color.FromArgb(215, 225, 236),
                    ButtonIcon = Color.FromArgb(65, 83, 103),
                    Success = Color.FromArgb(31, 133, 79),
                    Warning = Color.FromArgb(183, 103, 17),
                    Error = Color.FromArgb(185, 58, 52),
                    PrimaryAccent = Color.FromArgb(27, 146, 92),
                    SecondaryAccent = Color.FromArgb(36, 119, 188),
                    Surface = Color.FromArgb(255, 255, 255),
                    ControlBackground = Color.FromArgb(255, 255, 255),
                    ControlBorder = Color.FromArgb(182, 194, 207),
                    Grid = Color.FromArgb(225, 231, 237)
                };
            case ThemeMode.Graphite:
                return new ThemePalette
                {
                    BackgroundTop = Color.FromArgb(42, 47, 54),
                    BackgroundBottom = Color.FromArgb(24, 28, 34),
                    Border = Color.FromArgb(83, 92, 103),
                    Divider = Color.FromArgb(67, 75, 86),
                    PrimaryText = Color.FromArgb(246, 247, 249),
                    SecondaryText = Color.FromArgb(190, 199, 210),
                    Track = Color.FromArgb(70, 79, 90),
                    ButtonHover = Color.FromArgb(72, 82, 94),
                    ButtonIcon = Color.FromArgb(211, 220, 231),
                    Success = Color.FromArgb(137, 218, 114),
                    Warning = Color.FromArgb(255, 188, 91),
                    Error = Color.FromArgb(255, 123, 123),
                    PrimaryAccent = Color.FromArgb(112, 207, 138),
                    SecondaryAccent = Color.FromArgb(115, 190, 237),
                    Surface = Color.FromArgb(50, 56, 64),
                    ControlBackground = Color.FromArgb(46, 52, 59),
                    ControlBorder = Color.FromArgb(93, 103, 115),
                    Grid = Color.FromArgb(70, 78, 88)
                };
            default:
                return new ThemePalette
                {
                    BackgroundTop = Color.FromArgb(31, 38, 49),
                    BackgroundBottom = Color.FromArgb(14, 18, 25),
                    Border = Color.FromArgb(54, 64, 78),
                    Divider = Color.FromArgb(45, 53, 65),
                    PrimaryText = Color.FromArgb(244, 246, 248),
                    SecondaryText = Color.FromArgb(183, 193, 207),
                    Track = Color.FromArgb(49, 57, 70),
                    ButtonHover = Color.FromArgb(52, 62, 75),
                    ButtonIcon = Color.FromArgb(171, 182, 196),
                    Success = Color.FromArgb(165, 255, 117),
                    Warning = Color.FromArgb(255, 190, 96),
                    Error = Color.FromArgb(255, 119, 119),
                    PrimaryAccent = Color.FromArgb(165, 255, 117),
                    SecondaryAccent = Color.FromArgb(111, 196, 255),
                    Surface = Color.FromArgb(35, 42, 53),
                    ControlBackground = Color.FromArgb(29, 35, 44),
                    ControlBorder = Color.FromArgb(70, 80, 94),
                    Grid = Color.FromArgb(57, 66, 78)
                };
        }
    }

    private static ThemeMode DetectSystemTheme()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
            {
                object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                int lightTheme;
                if (value != null && Int32.TryParse(value.ToString(), out lightTheme))
                {
                    return lightTheme == 0 ? ThemeMode.Dark : ThemeMode.Light;
                }
            }
        }
        catch (System.Exception)
        {
            // 系统主题读取失败时使用深色，保证低亮度环境下文本仍有足够对比度。
        }
        return ThemeMode.Dark;
    }
}
