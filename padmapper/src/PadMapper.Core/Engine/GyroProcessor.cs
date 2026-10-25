using System.Numerics;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

/// <summary>
/// ジャイロ → マウス移動量。
/// 処理順（SPEC 7.1）：バイアス補正 → デッドゾーン → スムージング（指数移動平均）→ 加速カーブ → 感度 → 移動量。
/// バイアスは「一定時間ほぼ動かない間の平均」で自動推定する（ドリフト補正）。
/// </summary>
public sealed class GyroProcessor
{
    /// <summary>感度 1.0 のとき、1度回すと何ピクセル動くか。</summary>
    public const double BasePixelsPerDegree = 10;

    public GyroSettings Settings { get; set; } = new();

    /// <summary>静止とみなす揺れ幅（deg/s）。生値の分解能が粗い機種があるので少し広め。</summary>
    public double StillThresholdDps { get; set; } = 5.0;
    /// <summary>この時間だけ静止が続いたらバイアスを更新する。</summary>
    public double StillTimeMs { get; set; } = 1000;

    public Vector3 Bias { get; private set; }
    public bool IsCalibrated { get; private set; }
    public bool IsCalibratingManually => _manualUntil > 0;

    // 可視化用
    public Vector3 LastRaw { get; private set; }
    public Vector3 LastCorrected { get; private set; }
    public Vector2 LastOutputDps { get; private set; }
    public bool LastActive { get; private set; }

    private Vector3 _sum, _min, _max;
    private int _count;
    private double _windowStart = double.NaN;
    private double _manualUntil;
    private Vector2 _smoothed;
    private Vector3 _gravity;
    private bool _hasGravity;

    public void ResetBias()
    {
        Bias = Vector3.Zero;
        IsCalibrated = false;
        RestartWindow(double.NaN);
    }

    /// <summary>手動キャリブレーション：コントローラーを置いたまま durationMs 待つと、その間の平均をバイアスにする。</summary>
    public void StartManualCalibration(double nowMs, double durationMs = 1500)
    {
        _manualUntil = nowMs + durationMs;
        RestartWindow(nowMs);
    }

    /// <summary>1サンプル分を処理し、マウスの移動量（ピクセル、小数あり）を返す。</summary>
    public Vector2 Process(Vector3 gyro, Vector3? accel, double nowMs, double dtSec, bool active)
    {
        LastRaw = gyro;
        UpdateCalibration(gyro, nowMs);
        if (accel is Vector3 a && a.Length() > 0.3f)
        {
            _gravity = _hasGravity ? Vector3.Lerp(_gravity, a, 0.02f) : a;
            _hasGravity = true;
        }

        var c = gyro - Bias;
        LastCorrected = c;
        LastActive = active;
        var s = Settings;
        if (!active || dtSec <= 0)
        {
            _smoothed = Vector2.Zero;
            LastOutputDps = Vector2.Zero;
            return Vector2.Zero;
        }

        // 横方向の角速度
        float h = s.Axis switch
        {
            GyroAxis.Roll => c.Z,
            GyroAxis.Player when _hasGravity => Vector3.Dot(c, Vector3.Normalize(_gravity)),
            _ => c.Y,
        };
        // 右に回す → 右へ、上に傾ける → 上へ（SDL の座標系ではヨー・ピッチとも反時計回りが正）
        var v = new Vector2(-h, -c.X);

        // デッドゾーン（滑らかに：デッドゾーンの分だけ差し引く）
        float mag = v.Length();
        double dz = Math.Max(0, s.DeadzoneDps);
        v = mag <= dz || mag == 0 ? Vector2.Zero : v * (float)((mag - dz) / mag);

        // スムージング（時定数 = smoothing × 0.1 秒。サンプル周期に依存しない）
        double tau = Math.Clamp(s.Smoothing, 0, 1) * 0.1;
        float alpha = (float)(dtSec / (tau + dtSec));
        _smoothed += (v - _smoothed) * alpha;
        v = _smoothed;

        // 加速カーブ
        double factor = 1;
        if (s.Accel.Enabled)
        {
            double speed = v.Length();
            double span = Math.Max(1e-6, s.Accel.HighDps - s.Accel.LowDps);
            double t = Math.Clamp((speed - s.Accel.LowDps) / span, 0, 1);
            factor = 1 + (s.Accel.MaxMultiplier - 1) * t;
        }
        LastOutputDps = v * (float)factor;

        double px = BasePixelsPerDegree * dtSec * factor;
        double dx = v.X * px * s.SensitivityX * (s.InvertX ? -1 : 1);
        double dy = v.Y * px * s.SensitivityY * (s.InvertY ? -1 : 1);
        return new Vector2((float)dx, (float)dy);
    }

    private void UpdateCalibration(Vector3 g, double nowMs)
    {
        bool manual = _manualUntil > 0;
        if (!manual && !Settings.AutoCalibrate) return;
        if (double.IsNaN(_windowStart)) RestartWindow(nowMs);

        _sum += g;
        _count++;
        _min = _count == 1 ? g : Vector3.Min(_min, g);
        _max = _count == 1 ? g : Vector3.Max(_max, g);

        if (manual)
        {
            if (nowMs >= _manualUntil)
            {
                if (_count > 0)
                {
                    Bias = _sum / _count;
                    IsCalibrated = true;
                }
                _manualUntil = 0;
                RestartWindow(nowMs);
            }
            return;
        }

        var range = _max - _min;
        if (range.X > StillThresholdDps || range.Y > StillThresholdDps || range.Z > StillThresholdDps)
        {
            // 動いた：やり直し
            RestartWindow(nowMs);
            return;
        }
        if (nowMs - _windowStart >= StillTimeMs && _count >= 10)
        {
            var mean = _sum / _count;
            Bias = IsCalibrated ? Vector3.Lerp(Bias, mean, 0.5f) : mean;
            IsCalibrated = true;
            RestartWindow(nowMs);
        }
    }

    private void RestartWindow(double nowMs)
    {
        _sum = Vector3.Zero;
        _count = 0;
        _windowStart = nowMs;
    }
}
