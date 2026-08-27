using System;
using System.Threading.Tasks;
using System.Windows.Forms;

/// <summary>
/// 在 WinForms UI 线程上顺序触发刷新。它只负责计时，不读取凭据或处理业务结果。
/// </summary>
internal sealed class RefreshScheduler : IDisposable
{
    private readonly Func<Task> refreshAction;
    private readonly Timer timer;
    private bool disposed;

    public int IntervalMinutes { get; private set; }

    public bool IsRunning
    {
        get { return !disposed && timer.Enabled; }
    }

    public RefreshScheduler(int intervalMinutes, Func<Task> refreshAction)
    {
        if (refreshAction == null)
        {
            throw new ArgumentNullException("refreshAction");
        }

        this.refreshAction = refreshAction;
        timer = new Timer();
        timer.Tick += OnTick;
        SetInterval(intervalMinutes);
    }

    public void Start()
    {
        if (!disposed)
        {
            timer.Start();
        }
    }

    public void Stop()
    {
        if (!disposed)
        {
            timer.Stop();
        }
    }

    /// <summary>
    /// 应用新的预设周期；修改期间保持原有运行状态，不会意外启动已停止的调度器。
    /// </summary>
    public void SetInterval(int minutes)
    {
        if (disposed)
        {
            return;
        }

        bool wasRunning = timer.Enabled;
        IntervalMinutes = AppSettings.IsSupportedRefreshInterval(minutes) ? minutes : 5;
        timer.Interval = IntervalMinutes * 60 * 1000;
        timer.Stop();
        if (wasRunning)
        {
            timer.Start();
        }
    }

    private async void OnTick(object sender, EventArgs e)
    {
        try
        {
            await refreshAction();
        }
        catch (Exception)
        {
            // Timer 事件是 async void，刷新回调的最后一道异常边界不能让消息循环退出。
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timer.Stop();
        timer.Tick -= OnTick;
        timer.Dispose();
    }
}
