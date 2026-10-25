using System.Runtime.InteropServices;
using PadMapper.App.Native;
using PadMapper.Core.Logging;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Output;

/// <summary>
/// SendInput によるキーボード・マウス出力。キーはスキャンコード（KEYEVENTF_SCANCODE）、拡張キーは KEYEVENTF_EXTENDEDKEY 付き。
/// マウス移動は相対（MOUSEEVENTF_MOVE）。
/// </summary>
internal sealed class SendInputSink : IOutputSink
{
    /// <summary>本ソフトが送った入力の目印（dwExtraInfo）。</summary>
    public static readonly IntPtr ExtraInfo = new(0x50414450); // "PADP"

    private readonly ILog _log;
    private readonly int _size = Marshal.SizeOf<Win32.INPUT>();
    private int _failures;

    public SendInputSink(ILog log) => _log = log;

    public void Send(IReadOnlyList<OutputCommand> commands)
    {
        if (commands.Count == 0) return;
        var inputs = new List<Win32.INPUT>(commands.Count * 2);
        foreach (var c in commands) Append(inputs, c);
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        uint sent = Win32.SendInput((uint)arr.Length, arr, _size);
        if (sent != arr.Length)
        {
            // UIPI（権限の高いウィンドウ）やセキュアデスクトップではブロックされる
            if (_failures++ % 100 == 0)
                _log.Warn($"SendInput がブロックされました ({sent}/{arr.Length}, error {Marshal.GetLastWin32Error()})");
        }
    }

    private static void Append(List<Win32.INPUT> list, OutputCommand c)
    {
        switch (c.Kind)
        {
            case OutputKind.KeyDown:
            case OutputKind.KeyUp:
                if (!KeyCatalog.TryGet(c.Key, out var key)) return;
                uint flags = c.Kind == OutputKind.KeyUp ? Win32.KEYEVENTF_KEYUP : 0;
                var ki = new Win32.KEYBDINPUT { dwExtraInfo = ExtraInfo };
                if (key.Scan != 0)
                {
                    ki.wScan = key.Scan;
                    flags |= Win32.KEYEVENTF_SCANCODE;
                    if (key.Extended) flags |= Win32.KEYEVENTF_EXTENDEDKEY;
                }
                else ki.wVk = key.Vk;
                ki.dwFlags = flags;
                list.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.InputUnion { ki = ki } });
                break;

            case OutputKind.MouseDown:
            case OutputKind.MouseUp:
            {
                bool down = c.Kind == OutputKind.MouseDown;
                uint data = 0;
                uint f = c.Button switch
                {
                    MouseButton.Left => down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP,
                    MouseButton.Right => down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP,
                    MouseButton.Middle => down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP,
                    _ => down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP,
                };
                if (c.Button == MouseButton.X1) data = 1;
                if (c.Button == MouseButton.X2) data = 2;
                list.Add(Mouse(f, 0, 0, data));
                break;
            }

            case OutputKind.MouseMove:
                if (c.Dx != 0 || c.Dy != 0) list.Add(Mouse(Win32.MOUSEEVENTF_MOVE, c.Dx, c.Dy, 0));
                break;

            case OutputKind.Scroll:
                if (c.Wheel != 0)
                    list.Add(Mouse(c.Horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)c.Wheel)));
                break;
        }
    }

    private static Win32.INPUT Mouse(uint flags, int dx, int dy, uint data) => new()
    {
        type = Win32.INPUT_MOUSE,
        u = new Win32.InputUnion
        {
            mi = new Win32.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = ExtraInfo },
        },
    };
}
