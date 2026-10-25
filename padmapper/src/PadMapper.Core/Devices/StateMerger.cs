using PadMapper.Core.Model;

namespace PadMapper.Core.Devices;

/// <summary>
/// 1台のコントローラーが複数の HID コレクション（標準のゲームパッド + ベンダー独自）を持つとき、
/// それぞれの最新の状態を1つにまとめる。ボタンは OR、軸・ジャイロはそれを定義している側の値を使う。
/// </summary>
public static class StateMerger
{
    public static ControllerState Merge(IReadOnlyList<(DeviceParser parser, ControllerState state)> parts, double timestampMs)
    {
        var m = new ControllerState { TimestampMs = timestampMs };
        foreach (var (p, s) in parts)
        {
            m.Buttons |= s.Buttons;
            if (p.ProvidesAxis("LX")) m.LX = s.LX;
            if (p.ProvidesAxis("LY")) m.LY = s.LY;
            if (p.ProvidesAxis("RX")) m.RX = s.RX;
            if (p.ProvidesAxis("RY")) m.RY = s.RY;
            if (p.ProvidesAxis("LT")) m.LT = Math.Max(m.LT, s.LT);
            if (p.ProvidesAxis("RT")) m.RT = Math.Max(m.RT, s.RT);
            if (p.ProvidesGyro && s.HasGyro)
            {
                m.HasGyro = true;
                m.Gyro = s.Gyro;
            }
            if (p.ProvidesAccel && s.HasAccel)
            {
                m.HasAccel = true;
                m.Accel = s.Accel;
            }
        }
        return m;
    }
}
