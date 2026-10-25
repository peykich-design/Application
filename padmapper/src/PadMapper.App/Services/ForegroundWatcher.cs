using System.Text;
using PadMapper.App.Native;
using PadMapper.Core.Engine;
using PadMapper.Core.Logging;

namespace PadMapper.App.Services;

/// <summary>
/// フォアグラウンドウィンドウの変化を SetWinEventHook(EVENT_SYSTEM_FOREGROUND) で受け取る（ポーリングしない）。
/// UI スレッド（メッセージループのあるスレッド）で Start すること。
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly ILog _log;
    private readonly Func<bool> _logTitles;
    private readonly int _selfPid = Environment.ProcessId;
    private Win32.WinEventProc? _proc;
    private IntPtr _hook;

    public event Action<ForegroundInfo>? Changed;
    public ForegroundInfo Current { get; private set; } = ForegroundInfo.Empty;
    /// <summary>本ソフト以外で最後に前面だったアプリ（「いまのアプリに紐づける」に使う）。</summary>
    public ForegroundInfo LastExternal { get; private set; } = ForegroundInfo.Empty;
    public bool IsSelf(ForegroundInfo fg) => fg.ProcessId == _selfPid;

    public ForegroundWatcher(ILog log, Func<bool> logTitles)
    {
        _log = log;
        _logTitles = logTitles;
    }

    public void Start()
    {
        _proc = OnEvent;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero) _log.Warn("SetWinEventHook に失敗");
        Update(Win32.GetForegroundWindow());
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            Update(hwnd);
        }
        catch (Exception ex)
        {
            _log.Warn("フォアグラウンドの取得に失敗", ex);
        }
    }

    private void Update(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var info = Query(hwnd);
        if (info.ProcessId == 0) return;
        if (info == Current) return;
        Current = info;
        if (!IsSelf(info)) LastExternal = info;
        _log.Info(_logTitles()
            ? $"前面: {info.ExeName} \"{info.Title}\""
            : $"前面: {info.ExeName}");
        Changed?.Invoke(info);
    }

    public static ForegroundInfo Query(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return ForegroundInfo.Empty;
        string path = "";
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (Win32.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally
            {
                Win32.CloseHandle(h);
            }
        }
        int len = Win32.GetWindowTextLength(hwnd);
        var title = new StringBuilder(Math.Max(len + 1, 1));
        if (len > 0) Win32.GetWindowText(hwnd, title, title.Capacity);
        return new ForegroundInfo(Path.GetFileName(path), path, title.ToString(), (int)pid);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
