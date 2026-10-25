using System.Numerics;
using PadMapper.Core.Model;

namespace PadMapper.Core.Devices;

/// <summary>DeviceDefinition に従って生レポートを ControllerState に変換する。</summary>
public sealed class DeviceParser
{
    private readonly DeviceDefinition _def;
    private readonly (LogicalInput input, int offset, int mask, int? value)[] _buttons;
    private readonly int _minLength;

    public DeviceDefinition Definition => _def;
    public bool IsHid => _def.IsHid;

    /// <summary>この定義が値を持つ軸（複数のコレクションの状態を合成するときに使う）。</summary>
    public bool ProvidesAxis(string axis) => _def.Axes.Any(a => a.Axis.Equals(axis, StringComparison.OrdinalIgnoreCase));
    public bool ProvidesGyro => _def.Gyro.Count > 0;
    public bool ProvidesAccel => _def.Accel.Count > 0;

    public DeviceParser(DeviceDefinition def)
    {
        _def = def;
        var list = new List<(LogicalInput, int, int, int?)>();
        foreach (var b in def.Buttons)
            if (LogicalInputs.TryParse(b.Input, out var li))
                list.Add((li, b.Offset, b.Mask, b.Value));
        _buttons = list.ToArray();

        int need = Math.Max(def.MinLength, 1);
        foreach (var b in def.Buttons) need = Math.Max(need, b.Offset + 1);
        foreach (var a in def.Axes) need = Math.Max(need, MaxOffset(a.Offset, a.OffsetHi, a.Format) + 1);
        foreach (var s in def.Gyro.Concat(def.Accel)) need = Math.Max(need, MaxOffset(s.Offset, s.OffsetHi, s.Format) + 1);
        _minLength = need;
    }

    private static int MaxOffset(int offset, int? hi, string format) =>
        Is16(format) ? Math.Max(offset, hi ?? offset + 1) : offset;

    private static bool Is16(string format) => format is "u16" or "s16";

    /// <summary>このレポートを解析できれば true を返し、state を上書きする。</summary>
    public bool TryParse(ReadOnlySpan<byte> report, ControllerState state)
    {
        if (_def.IsHid || report.Length < _minLength) return false;
        if (_def.ReportId is int rid && report[0] != rid) return false;
        foreach (var sig in _def.Signature)
            if (sig.Offset >= report.Length || report[sig.Offset] != sig.Value) return false;

        var buttons = InputSet.Empty;
        foreach (var (input, offset, mask, value) in _buttons)
        {
            int b = report[offset] & mask;
            bool pressed = value is int v ? b == v : b != 0;
            if (pressed) buttons = buttons.With(input);
        }
        state.Buttons = buttons;

        foreach (var a in _def.Axes)
        {
            double raw = Read(report, a.Offset, a.OffsetHi, a.Format);
            switch (a.Axis.ToUpperInvariant())
            {
                case "LX": state.LX = Stick(raw, a); break;
                case "LY": state.LY = Stick(raw, a); break;
                case "RX": state.RX = Stick(raw, a); break;
                case "RY": state.RY = Stick(raw, a); break;
                case "LT": state.LT = Trigger(raw, a); break;
                case "RT": state.RT = Trigger(raw, a); break;
            }
        }

        if (_def.Gyro.Count > 0)
        {
            state.HasGyro = true;
            state.Gyro = ReadVector(report, _def.Gyro);
        }
        if (_def.Accel.Count > 0)
        {
            state.HasAccel = true;
            state.Accel = ReadVector(report, _def.Accel);
        }
        return true;
    }

    /// <summary>HID 解析済みの値から状態を作る（Kind = "hid" の定義用）。</summary>
    public bool TryParseHid(HidSnapshot snap, ControllerState state)
    {
        var buttons = InputSet.Empty;
        foreach (var b in _def.Buttons)
        {
            if (!LogicalInputs.TryParse(b.Input, out var li)) continue;
            bool pressed = b.HidButton is int n ? snap.Buttons.Contains(n)
                : b.HidHat != null && HidSnapshot.HatHas(snap.Hat, b.HidHat);
            if (pressed) buttons = buttons.With(li);
        }
        state.Buttons = buttons;
        foreach (var a in _def.Axes)
        {
            if (a.HidUsage is not int u || !snap.Values.TryGetValue(u, out var raw)) continue;
            switch (a.Axis.ToUpperInvariant())
            {
                case "LX": state.LX = Stick(raw, a); break;
                case "LY": state.LY = Stick(raw, a); break;
                case "RX": state.RX = Stick(raw, a); break;
                case "RY": state.RY = Stick(raw, a); break;
                case "LT": state.LT = Trigger(raw, a); break;
                case "RT": state.RT = Trigger(raw, a); break;
            }
        }
        return true;
    }

    private static Vector3 ReadVector(ReadOnlySpan<byte> report, List<SensorMap> maps)
    {
        var v = Vector3.Zero;
        foreach (var s in maps)
        {
            float value = (float)(Read(report, s.Offset, s.OffsetHi, s.Format) * s.Scale);
            switch (s.Axis.ToLowerInvariant())
            {
                case "x": case "pitch": v.X = value; break;
                case "y": case "yaw": v.Y = value; break;
                case "z": case "roll": v.Z = value; break;
            }
        }
        return v;
    }

    internal static double Read(ReadOnlySpan<byte> r, int offset, int? offsetHi, string format)
    {
        switch (format)
        {
            case "s8": return (sbyte)r[offset];
            case "u16": return (ushort)(r[offset] | (r[offsetHi ?? offset + 1] << 8));
            case "s16": return (short)(r[offset] | (r[offsetHi ?? offset + 1] << 8));
            default: return r[offset];
        }
    }

    private static float Stick(double raw, AxisMap a)
    {
        double center = a.Center ?? (a.Min + a.Max) / 2;
        double v;
        if (raw >= center)
        {
            double range = a.Max - center;
            v = range <= 0 ? 0 : (raw - center) / range;
        }
        else
        {
            double range = center - a.Min;
            v = range <= 0 ? 0 : (raw - center) / range;
        }
        v = Math.Clamp(v, -1, 1);
        return (float)(a.Invert ? -v : v);
    }

    private static float Trigger(double raw, AxisMap a)
    {
        double range = a.Max - a.Min;
        double v = range == 0 ? 0 : Math.Clamp((raw - a.Min) / range, 0, 1);
        return (float)(a.Invert ? 1 - v : v);
    }
}
