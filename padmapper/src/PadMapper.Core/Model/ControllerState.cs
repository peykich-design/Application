using System.Numerics;

namespace PadMapper.Core.Model;

/// <summary>
/// 1回分のコントローラーの状態。機種ごとの違いは DeviceDefinition の解析で吸収する。
/// 軸は -1..1（スティックの Y は下が正）、トリガーは 0..1。
/// ジャイロは deg/s で、X=ピッチ、Y=ヨー、Z=ロール（SDL と同じ向き）。
/// </summary>
public sealed class ControllerState
{
    /// <summary>ボタン（デバイスが直接報告するもの。LT/RT のアナログ判定やスティック方向は含まない）。</summary>
    public InputSet Buttons;

    public float LX, LY, RX, RY;
    public float LT, RT;

    public bool HasGyro;
    public Vector3 Gyro;

    public bool HasAccel;
    /// <summary>加速度（G 単位）。取れる機種のみ。</summary>
    public Vector3 Accel;

    /// <summary>受信時刻（ミリ秒、単調増加）。</summary>
    public double TimestampMs;

    public ControllerState Clone() => (ControllerState)MemberwiseClone();
}
