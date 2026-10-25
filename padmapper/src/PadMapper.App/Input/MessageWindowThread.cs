using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PadMapper.App.Native;
using PadMapper.Core.Logging;

namespace PadMapper.App.Input;

/// <summary>
/// 専用スレッドで動くメッセージ専用ウィンドウ。Raw Input（WM_INPUT）、デバイスの抜き差し、ホットキーを受け取る。
/// UI スレッドとは別なので、画面が重くても入力の受信は遅れない。
/// </summary>
internal sealed class MessageWindowThread : IDisposable
{
    private const uint WM_INVOKE = Win32.WM_APP + 1;
    private const string ClassName = "PadMapper.MessageWindow";

    private readonly ILog _log;
    private readonly ConcurrentQueue<Action> _invokes = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Win32.WndProc? _proc; // GC に回収されないように保持
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _devNotify;
    private byte[] _buffer = new byte[1024];
    private readonly int _headerSize = Marshal.SizeOf<Win32.RAWINPUTHEADER>();

    /// <summary>HID の生レポート（デバイスハンドル, レポート）。</summary>
    public event Action<IntPtr, byte[]>? HidReport;
    /// <summary>Raw Input のデバイス到着/削除（true=到着）。</summary>
    public event Action<bool, IntPtr>? RawDeviceChanged;
    /// <summary>WM_DEVICECHANGE（HID インターフェースの抜き差し）。</summary>
    public event Action? DeviceInterfaceChanged;
    public event Action<int>? HotKey;

    public IntPtr Hwnd => _hwnd;

    public MessageWindowThread(ILog log) => _log = log;

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PadMapper.Input", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
    }

    /// <summary>ウィンドウのスレッドで処理を実行する（Raw Input の登録やホットキーはここで行う）。</summary>
    public void Invoke(Action action)
    {
        _invokes.Enqueue(action);
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            var hInstance = Win32.GetModuleHandle(null);
            _proc = WndProc;
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInstance,
                lpszClassName = ClassName,
            };
            Win32.RegisterClassEx(ref wc);
            _hwnd = Win32.CreateWindowEx(0, ClassName, "PadMapper", 0, 0, 0, 0, 0, Win32.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                _log.Error($"メッセージウィンドウを作れません (error {Marshal.GetLastWin32Error()})");
                return;
            }
            RegisterHidInterfaceNotification();
        }
        catch (Exception ex)
        {
            _log.Error("メッセージウィンドウの初期化に失敗", ex);
            return;
        }
        finally
        {
            _ready.Set();
        }

        // 初期化前に積まれた処理を実行
        RunInvokes();
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
        if (_devNotify != IntPtr.Zero) Win32.UnregisterDeviceNotification(_devNotify);
    }

    private void RegisterHidInterfaceNotification()
    {
        Win32.HidD_GetHidGuid(out var guid);
        var filter = new Win32.DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<Win32.DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = Win32.DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = guid,
        };
        _devNotify = Win32.RegisterDeviceNotification(_hwnd, ref filter, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private void RunInvokes()
    {
        while (_invokes.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { _log.Error("入力スレッドの処理で例外", ex); }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case Win32.WM_INPUT:
                    HandleInput(lParam);
                    break; // WM_INPUT は DefWindowProc に渡して後始末させる
                case Win32.WM_INPUT_DEVICE_CHANGE:
                    RawDeviceChanged?.Invoke(wParam.ToInt32() == Win32.GIDC_ARRIVAL, lParam);
                    return IntPtr.Zero;
                case Win32.WM_DEVICECHANGE:
                    DeviceInterfaceChanged?.Invoke();
                    break;
                case Win32.WM_HOTKEY:
                    HotKey?.Invoke(wParam.ToInt32());
                    return IntPtr.Zero;
                case WM_INVOKE:
                    RunInvokes();
                    return IntPtr.Zero;
                case Win32.WM_CLOSE:
                    Win32.DestroyWindow(hWnd);
                    return IntPtr.Zero;
                case Win32.WM_DESTROY:
                    Win32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"メッセージ処理で例外 (msg=0x{msg:X})", ex);
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private unsafe void HandleInput(IntPtr hRawInput)
    {
        uint size = 0;
        Win32.GetRawInputData(hRawInput, Win32.RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize);
        if (size == 0) return;
        if (_buffer.Length < size) _buffer = new byte[Math.Max(size, _buffer.Length * 2)];
        fixed (byte* p = _buffer)
        {
            uint got = Win32.GetRawInputData(hRawInput, Win32.RID_INPUT, (IntPtr)p, ref size, (uint)_headerSize);
            if (got == uint.MaxValue || got < _headerSize) return;
            var header = Marshal.PtrToStructure<Win32.RAWINPUTHEADER>((IntPtr)p);
            if (header.dwType != Win32.RIM_TYPEHID) return;
            uint sizeHid = *(uint*)(p + _headerSize);
            uint count = *(uint*)(p + _headerSize + 4);
            int offset = _headerSize + 8;
            if (sizeHid == 0 || offset + sizeHid * count > got) return;
            var handler = HidReport;
            if (handler == null) return;
            for (int i = 0; i < count; i++)
            {
                var report = new byte[sizeHid];
                Marshal.Copy((IntPtr)(p + offset + i * sizeHid), report, 0, (int)sizeHid);
                handler(header.hDevice, report);
            }
        }
    }

    // ---------- Raw Input の補助（ウィンドウのスレッドから呼ぶこと） ----------

    public bool Register(IEnumerable<(int usagePage, int usage)> collections)
    {
        var devices = collections.Distinct().Select(c => new Win32.RAWINPUTDEVICE
        {
            usUsagePage = (ushort)c.usagePage,
            usUsage = (ushort)c.usage,
            dwFlags = Win32.RIDEV_INPUTSINK | Win32.RIDEV_DEVNOTIFY,
            hwndTarget = _hwnd,
        }).ToArray();
        if (devices.Length == 0) return true;
        bool ok = Win32.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());
        if (!ok) _log.Warn($"Raw Input の登録に失敗 (error {Marshal.GetLastWin32Error()})");
        return ok;
    }

    public static string? GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        Win32.GetRawInputDeviceInfo(hDevice, Win32.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size * 2 + 2);
        try
        {
            uint got = Win32.GetRawInputDeviceInfo(hDevice, Win32.RIDI_DEVICENAME, buf, ref size);
            if (got == uint.MaxValue) return null;
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _thread = null;
        _ready.Dispose();
    }
}
