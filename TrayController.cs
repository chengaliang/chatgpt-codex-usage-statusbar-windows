using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// 管理状态栏对应的通知区域图标。托盘菜单只通过回调访问主窗口，避免把 OAuth、额度数据或业务逻辑复制到托盘层。
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu;
    private bool disposed;

    public TrayController(
        Action showWindow,
        Action refresh,
        Action showSettings,
        Action runDiagnostics,
        Action exitApplication)
        : this(
            showWindow,
            showWindow,
            refresh,
            showSettings,
            CreateEventHandler(runDiagnostics),
            delegate { },
            exitApplication)
    {
    }

    public TrayController(
        Action showWindow,
        Action refresh,
        Action showSettings,
        EventHandler runDiagnostics,
        Action openProject,
        Action exitApplication)
        : this(
            showWindow,
            showWindow,
            refresh,
            showSettings,
            runDiagnostics,
            openProject,
            exitApplication)
    {
    }

    public TrayController(
        Action showWindow,
        Action showDetails,
        Action refresh,
        Action showSettings,
        EventHandler runDiagnostics,
        Action openProject,
        Action exitApplication)
    {
        if (showWindow == null)
        {
            throw new ArgumentNullException("showWindow");
        }
        if (showDetails == null)
        {
            throw new ArgumentNullException("showDetails");
        }
        if (refresh == null)
        {
            throw new ArgumentNullException("refresh");
        }
        if (showSettings == null)
        {
            throw new ArgumentNullException("showSettings");
        }
        if (runDiagnostics == null)
        {
            throw new ArgumentNullException("runDiagnostics");
        }
        if (openProject == null)
        {
            throw new ArgumentNullException("openProject");
        }
        if (exitApplication == null)
        {
            throw new ArgumentNullException("exitApplication");
        }

        menu = new ContextMenuStrip();
        menu.ShowImageMargin = false;
        menu.Items.Add(CreateItem("显示状态栏", delegate { showWindow(); }));
        menu.Items.Add(CreateItem("打开 Usage Hub", delegate { showDetails(); }));
        menu.Items.Add(CreateItem("立即刷新", delegate { refresh(); }));
        menu.Items.Add(CreateItem("设置", delegate { showSettings(); }));
        menu.Items.Add(CreateItem("诊断中心", runDiagnostics));
        menu.Items.Add(CreateItem("打开项目主页", delegate { openProject(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(CreateItem("退出", delegate { exitApplication(); }));

        notifyIcon = new NotifyIcon();
        notifyIcon.Icon = SystemIcons.Application;
        notifyIcon.Text = "ChatGPT/Codex 额度状态栏";
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.Visible = true;
        notifyIcon.DoubleClick += delegate(object sender, EventArgs args) { showWindow(); };
        UiTheme.StyleMenu(menu, ThemePalette.Create(ThemeMode.Dark));
    }

    private static EventHandler CreateEventHandler(Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException("runDiagnostics");
        }
        return delegate(object sender, EventArgs args) { action(); };
    }

    public void SetStatus(string text)
    {
        if (disposed)
        {
            return;
        }

        string value = string.IsNullOrWhiteSpace(text) ? "ChatGPT/Codex 额度状态栏" : text.Trim();
        // NotifyIcon 的 Text 在不同 Windows 版本上限制为 63 个字符，超长时截断而不是抛异常。
        if (value.Length > 63)
        {
            value = value.Substring(0, 63);
        }
        notifyIcon.Text = value;
    }

    public void ShowNotification(string title, string message)
    {
        if (disposed)
        {
            return;
        }

        string safeTitle = string.IsNullOrWhiteSpace(title) ? "ChatGPT/Codex" : title.Trim();
        string safeMessage = string.IsNullOrWhiteSpace(message) ? "额度状态发生变化" : message.Trim();
        if (safeTitle.Length > 63)
        {
            safeTitle = safeTitle.Substring(0, 63);
        }
        if (safeMessage.Length > 255)
        {
            safeMessage = safeMessage.Substring(0, 255);
        }
        notifyIcon.ShowBalloonTip(4000, safeTitle, safeMessage, ToolTipIcon.Info);
    }

    private static ToolStripMenuItem CreateItem(string text, EventHandler handler)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(text);
        item.AutoSize = true;
        item.Click += handler;
        return item;
    }

    /// <summary>
    /// 主窗口主题切换后同步托盘菜单，避免右键菜单仍停留在旧的系统默认配色。
    /// </summary>
    public void ApplyTheme(ThemePalette palette)
    {
        if (disposed || palette == null)
        {
            return;
        }
        UiTheme.StyleMenu(menu, palette);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        menu.Dispose();
    }
}
