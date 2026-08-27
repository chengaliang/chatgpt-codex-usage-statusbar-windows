using System;
using System.Runtime.InteropServices;

/// <summary>
/// 在支持的 Windows 版本启用每显示器 DPI 感知，避免状态栏在不同缩放屏幕之间出现模糊或位置漂移。
/// </summary>
internal static class DpiSupport
{
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public static void Enable()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
            {
                return;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 7/8 没有 PerMonitorV2 API，继续尝试旧版 DPI 感知接口。
        }
        catch (DllNotFoundException)
        {
            return;
        }

        try
        {
            SetProcessDPIAware();
        }
        catch (EntryPointNotFoundException)
        {
            // 极旧系统没有 DPI 接口时保持默认行为，不能阻止状态栏启动。
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 运行环境仅用于静态检查，忽略平台 API 缺失。
        }
    }
}
