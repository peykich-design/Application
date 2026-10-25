using System.Runtime.InteropServices;
using PadMapper.App.Native;
using PadMapper.Core.Devices;
using PadMapper.Core.Logging;

namespace PadMapper.App.Input;

/// <summary>
/// 標準の HID ゲームパッド（Generic Desktop の Gamepad / Joystick）のレポートを、Windows の HID 解析（HidP）で読む。
/// ボタンは番号、軸は Usage（X, Y, Z, Rx, Ry, Rz …）、ハットスイッチは方向で取り出す。
/// レポートのバイト位置を知らなくても読めるので、DInput モードのコントローラーはこれで認識できる。
/// </summary>
internal sealed class HidCollection : IDisposable
{
    private const ushort PageGenericDesktop = 0x01, PageButton = 0x09;

    private IntPtr _preparsed;
    private readonly List<(ushort usage, ushort link, HidRange range, int bitSize, bool signed)> _values = new();
    private readonly int _maxButtons;
    private ushort[] _usageBuffer;

    public string Path { get; }
    public int ButtonCount => _maxButtons;
    public IReadOnlyList<int> AxisUsages => _values.Select(v => (int)v.usage).ToList();

    private HidCollection(string path, IntPtr preparsed)
    {
        Path = path;
        _preparsed = preparsed;
        _maxButtons = Math.Max(0, Win32.HidP_MaxUsageListLength(Win32.HidP_Input, PageButton, preparsed));
        _usageBuffer = new ushort[Math.Max(32, _maxButtons)];

        ushort count = 64;
        var caps = new Win32.HIDP_VALUE_CAPS[count];
        if (Win32.HidP_GetValueCaps(Win32.HidP_Input, caps, ref count, preparsed) != Win32.HIDP_STATUS_SUCCESS) return;
        for (int i = 0; i < count; i++)
        {
            var c = caps[i];
            if (c.UsagePage != PageGenericDesktop) continue;
            int last = c.IsRange != 0 ? c.UsageMax : c.UsageMin;
            for (int u = c.UsageMin; u <= last; u++)
            {
                if (_values.Any(v => v.usage == u)) continue;
                int min = c.LogicalMin, max = c.LogicalMax;
                // Logical Max が符号付きで負に読める場合（例：0〜65535 が -1 と記述される）は符号なしとして扱う
                if (max < min && c.BitSize < 32) max = (int)((1L << c.BitSize) - 1);
                _values.Add(((ushort)u, c.LinkCollection, new HidRange(min, max), c.BitSize, min < 0));
            }
        }
    }

    /// <summary>デバイスを開いて解析用のデータを取る。標準のゲームパッドでなければ null。</summary>
    public static HidCollection? Open(HidDeviceInfo d, ILog log)
    {
        if (d.UsagePage != PageGenericDesktop || d.Usage is not (0x04 or 0x05 or 0x08)) return null;
        try
        {
            using var h = Win32.CreateFile(d.Path, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid || !Win32.HidD_GetPreparsedData(h, out var pp)) return null;
            return new HidCollection(d.Path, pp);
        }
        catch (Exception ex)
        {
            log.Warn($"HID の解析情報を取れません: {d.Product}", ex);
            return null;
        }
    }

    /// <summary>1レポートを解析する（レポート ID の先頭バイトを含むデータを渡す）。</summary>
    public HidSnapshot? Parse(byte[] report)
    {
        if (_preparsed == IntPtr.Zero) return null;
        var snap = new HidSnapshot();
        uint len = (uint)_usageBuffer.Length;
        int status = Win32.HidP_GetUsages(Win32.HidP_Input, PageButton, 0, _usageBuffer, ref len, _preparsed, report, (uint)report.Length);
        if (status == Win32.HIDP_STATUS_SUCCESS)
            for (int i = 0; i < len; i++) snap.Buttons.Add(_usageBuffer[i]);
        else if (status != HIDP_STATUS_INCOMPATIBLE_REPORT_ID)
            return null;

        foreach (var (usage, link, range, bits, signed) in _values)
        {
            if (Win32.HidP_GetUsageValue(Win32.HidP_Input, PageGenericDesktop, link, usage, out uint raw, _preparsed, report, (uint)report.Length)
                != Win32.HIDP_STATUS_SUCCESS) continue;
            int v = (int)raw;
            // 符号付きの値は、ビット数に合わせて符号を広げる
            if (signed && bits is > 0 and < 32 && (raw & (1u << (bits - 1))) != 0) v = (int)(raw | ~((1u << bits) - 1));
            if (usage == HidSnapshot.UsageHat)
            {
                // ハットは Logical Min〜Max の範囲外が「中立」。範囲を 8 方向（0=上）に直す
                int span = range.Max - range.Min + 1;
                snap.Hat = v < range.Min || v > range.Max ? null : span >= 8 ? (v - range.Min) * 8 / span : null;
            }
            else snap.Values[usage] = range.Normalize(v);
        }
        return snap;
    }

    // このレポートに含まれないページ（別のレポート ID）のときに返るステータス
    private const int HIDP_STATUS_INCOMPATIBLE_REPORT_ID = unchecked((int)0xC011000A);

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero) Win32.HidD_FreePreparsedData(_preparsed);
        _preparsed = IntPtr.Zero;
    }
}
