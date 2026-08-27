using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

/// <summary>
/// 使用隐藏 NativeWindow 接收 Ctrl+Alt+U。注册失败只返回 false，不阻塞状态栏或网络查询。
/// </summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private static int nextId;
    private readonly Action callback;
    private readonly int id;
    private bool registered;
    private bool disposed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public GlobalHotkey(Action callback)
    {
        this.callback = callback;
        id = Interlocked.Increment(ref nextId);
    }

    public string ShortcutText
    {
        get { return "Ctrl+Alt+U"; }
    }

    public bool IsRegistered
    {
        get { return registered; }
    }

    /// <summary>
    /// 在当前进程注册固定快捷键；重复调用是幂等的，冲突由 Windows 返回失败。
    /// </summary>
    public bool TryRegister()
    {
        if (disposed || registered)
        {
            return registered;
        }

        try
        {
            if (Handle == IntPtr.Zero)
            {
                CreateHandle(new CreateParams());
            }
            registered = RegisterHotKey(Handle, id, ModControl | ModAlt, (uint)Keys.U);
        }
        catch (Win32Exception)
        {
            registered = false;
        }
        catch (Exception)
        {
            registered = false;
        }
        return registered;
    }

    public void Unregister()
    {
        if (!registered || Handle == IntPtr.Zero)
        {
            registered = false;
            return;
        }

        try
        {
            UnregisterHotKey(Handle, id);
        }
        catch (Exception)
        {
            // 进程退出时句柄可能已由系统回收，不能让清理异常影响主窗口关闭。
        }
        registered = false;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == id && callback != null)
        {
            try
            {
                callback();
            }
            catch (Exception)
            {
                // 快捷键回调位于消息循环中，回调失败时保持进程继续运行。
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Unregister();
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
        GC.SuppressFinalize(this);
    }
}
