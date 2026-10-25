using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PadMapper.App.Native;

namespace PadMapper.App.Services;

/// <summary>
/// 二重起動防止（名前付き Mutex）。2つ目が起動したら、名前付きイベントで既存のウィンドウを前面に出してもらう。
/// イベントは整合性レベルを「低」にして作るので、昇格していないプロセスからも合図できる。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PadMapper.SingleInstance";
    private const string EventName = @"Local\PadMapper.ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private Thread? _listener;
    private volatile bool _disposed;

    private SingleInstance() { }

    /// <summary>最初のインスタンスなら取得できる。既に動いていれば null。</summary>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            var m = new Mutex(true, MutexName, out bool created);
            if (!created)
            {
                m.Dispose();
                return null;
            }
            return new SingleInstance { _mutex = m };
        }
        catch (UnauthorizedAccessException)
        {
            // 昇格したインスタンスが持っている
            return null;
        }
    }

    public void ListenForShowRequests(Action onShow)
    {
        _event = CreatePermissiveEvent();
        if (_event == null) return;
        _listener = new Thread(() =>
        {
            while (!_disposed)
            {
                try
                {
                    if (_event.WaitOne() && !_disposed) onShow();
                }
                catch
                {
                    return;
                }
            }
        }) { IsBackground = true, Name = "PadMapper.SingleInstance" };
        _listener.Start();
    }

    /// <summary>既存のインスタンスにウィンドウ表示を頼む。</summary>
    public static bool SignalExisting()
    {
        using var h = Win32.OpenEvent(Win32.EVENT_MODIFY_STATE, false, EventName);
        return !h.IsInvalid && Win32.SetEvent(h);
    }

    private static EventWaitHandle? CreatePermissiveEvent()
    {
        // 全員にアクセスを許可し、整合性レベルを「低」にする
        const string sddl = "D:(A;;GA;;;WD)(A;;GA;;;AN)S:(ML;;NW;;;LW)";
        if (!Win32.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var sd, IntPtr.Zero))
            return new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        try
        {
            var sa = new Win32.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = false,
            };
            SafeWaitHandle handle = Win32.CreateEvent(ref sa, false, false, EventName);
            if (handle.IsInvalid) return null;
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = handle };
            return ev;
        }
        finally
        {
            Win32.LocalFree(sd);
        }
    }

    /// <summary>タスク経由で再起動する前など、明示的に手放す。</summary>
    public void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _event?.Set(); } catch { }
        Release();
        _event?.Dispose();
    }
}
