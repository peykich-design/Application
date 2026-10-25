using PadMapper.App.Native;
using PadMapper.Core.Devices;
using PadMapper.Core.Diagnostics;
using PadMapper.Core.Logging;

namespace PadMapper.App.Input;

/// <summary>
/// HID デバイスの直接読み取り（Raw Input で必要なレポートが取れない場合のフォールバック）。
/// FILE_SHARE_READ | FILE_SHARE_WRITE で共有して開くので、ゲームが同じデバイスを読むのを妨げない。
/// </summary>
internal sealed class HidReader : IDisposable
{
    private readonly HidDeviceInfo _device;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private FileStream? _stream;

    public event Action<string, byte[]>? Report;
    public event Action<string>? Stopped;
    public string Path => _device.Path;

    public HidReader(HidDeviceInfo device, ILog log)
    {
        _device = device;
        _log = log;
    }

    public bool Start()
    {
        var h = Win32.CreateFile(_device.Path, Win32.GENERIC_READ, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid)
        {
            _log.Warn($"HID を開けません: {_device.Product} ({_device.UsageText}) error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            h.Dispose();
            return false;
        }
        _stream = new FileStream(h, FileAccess.Read, 0, isAsync: true);
        _ = Task.Run(ReadLoop);
        return true;
    }

    private async Task ReadLoop()
    {
        int len = Math.Max(_device.InputReportLength, 2);
        var buf = new byte[len];
        try
        {
            while (!_cts.IsCancellationRequested && _stream != null)
            {
                int n = await _stream.ReadAsync(buf.AsMemory(0, len), _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                Report?.Invoke(_device.Path, buf.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Info($"HID 読み取り終了: {_device.Product} ({ex.Message})");
        }
        Stopped?.Invoke(_device.Path);
    }

    /// <summary>
    /// 出力レポート（初期化コマンド）を1回だけ送る。SPEC 4章の例外条項：実機で必要と分かった場合のみ、設定でオンにしたときだけ使う。
    /// </summary>
    public static bool SendOutputReport(HidDeviceInfo device, string hex, ILog log)
    {
        try
        {
            var data = ReportDiff.FromHex(hex);
            if (data.Length == 0) return false;
            int len = Math.Max(device.OutputReportLength, data.Length);
            var buf = new byte[len];
            Array.Copy(data, buf, data.Length);
            using var h = Win32.CreateFile(device.Path, Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return false;
            bool ok = Win32.HidD_SetOutputReport(h, buf, buf.Length);
            log.Info($"初期化コマンド送信 {(ok ? "成功" : "失敗")}: {device.Product} {hex}");
            return ok;
        }
        catch (Exception ex)
        {
            log.Warn("初期化コマンドの送信に失敗", ex);
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
