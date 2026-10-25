using System.Numerics;
using PadMapper.Core.Model;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

/// <summary>スティック・トリガーの計算（デッドゾーン、レスポンスカーブ、仮想入力の生成）。</summary>
public static class StickProcessor
{
    /// <summary>円形デッドゾーンを適用し、0..1 に正規化したベクトルを返す。</summary>
    public static Vector2 ApplyDeadzone(Vector2 v, double inner, double outer)
    {
        float mag = v.Length();
        if (mag <= inner || mag == 0) return Vector2.Zero;
        double usable = Math.Max(1e-6, 1 - outer - inner);
        double scaled = Math.Clamp((mag - inner) / usable, 0, 1);
        return v / mag * (float)scaled;
    }

    /// <summary>レスポンスカーブ（大きさに指数を掛ける）。</summary>
    public static Vector2 ApplyCurve(Vector2 v, double exponent)
    {
        float mag = v.Length();
        if (mag == 0) return v;
        double curved = Math.Pow(Math.Min(1, mag), Math.Max(0.1, exponent));
        return v / mag * (float)curved;
    }

    public static Vector2 Process(float x, float y, StickSettings s)
    {
        var v = ApplyDeadzone(new Vector2(x, y), s.Deadzone, s.OuterDeadzone);
        v = ApplyCurve(v, s.Curve);
        if (s.InvertY) v.Y = -v.Y;
        return v;
    }

    /// <summary>
    /// デバイスのボタンに、トリガーのしきい値判定（LT/RT/LT2/RT2）とスティック方向（LSUp など）を足した入力集合を作る。
    /// 方向はヒステリシス付き（押す：threshold、離す：threshold - 0.1）。
    /// </summary>
    public static InputSet Derive(ControllerState s, Profile p, InputSet previous)
    {
        var set = s.Buttons;
        var t = p.Triggers;
        if (s.LT >= t.Threshold) set = set.With(LogicalInput.LT);
        if (s.RT >= t.Threshold) set = set.With(LogicalInput.RT);
        if (s.LT >= t.SecondStage) set = set.With(LogicalInput.LT2);
        if (s.RT >= t.SecondStage) set = set.With(LogicalInput.RT2);

        double on = p.Sticks.DirectionThreshold;
        double off = Math.Max(0.05, on - 0.1);
        set = Direction(set, previous, LogicalInput.LSLeft, -s.LX, on, off);
        set = Direction(set, previous, LogicalInput.LSRight, s.LX, on, off);
        set = Direction(set, previous, LogicalInput.LSUp, -s.LY, on, off);
        set = Direction(set, previous, LogicalInput.LSDown, s.LY, on, off);
        set = Direction(set, previous, LogicalInput.RSLeft, -s.RX, on, off);
        set = Direction(set, previous, LogicalInput.RSRight, s.RX, on, off);
        set = Direction(set, previous, LogicalInput.RSUp, -s.RY, on, off);
        set = Direction(set, previous, LogicalInput.RSDown, s.RY, on, off);
        return set;
    }

    private static InputSet Direction(InputSet set, InputSet prev, LogicalInput input, float value, double on, double off)
    {
        bool was = prev.Has(input);
        bool now = was ? value >= off : value >= on;
        return now ? set.With(input) : set;
    }
}
