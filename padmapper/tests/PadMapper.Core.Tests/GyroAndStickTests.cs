using System.Numerics;
using PadMapper.Core.Engine;
using PadMapper.Core.Model;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Tests;

public class GyroAndStickTests
{
    private static GyroProcessor Gyro(Action<GyroSettings>? edit = null)
    {
        var s = new GyroSettings { Enabled = true, Smoothing = 0, DeadzoneDps = 0 };
        edit?.Invoke(s);
        return new GyroProcessor { Settings = s };
    }

    [Fact]
    public void AutoCalibration_RemovesDrift()
    {
        var g = Gyro();
        var drift = new Vector3(1.5f, -2f, 0.5f);
        double t = 0;
        // 静止したまま 2 秒
        for (int i = 0; i < 1000; i++, t += 2)
            g.Process(drift, null, t, 0.002, active: false);
        Assert.True(g.IsCalibrated);
        // 補正後はカーソルが流れない
        double total = 0;
        for (int i = 0; i < 500; i++, t += 2)
        {
            var m = g.Process(drift, null, t, 0.002, active: true);
            total += Math.Abs(m.X) + Math.Abs(m.Y);
        }
        Assert.True(total < 0.01, $"drift total {total}");
    }

    [Fact]
    public void Movement_PreventsCalibration()
    {
        var g = Gyro();
        double t = 0;
        for (int i = 0; i < 1000; i++, t += 2)
            g.Process(new Vector3(0, i % 2 == 0 ? 30 : -30, 0), null, t, 0.002, false);
        Assert.False(g.IsCalibrated);
    }

    [Fact]
    public void Sensitivity_ScalesOutput_AndYawRightMovesRight()
    {
        var g = Gyro(s => { s.SensitivityX = 2; s.AutoCalibrate = false; });
        // ヨー -90 deg/s（SDL の座標系で右回り）を 1 秒
        double x = 0;
        for (int i = 0; i < 500; i++)
            x += g.Process(new Vector3(0, -90, 0), null, i * 2, 0.002, true).X;
        Assert.Equal(90 * GyroProcessor.BasePixelsPerDegree * 2, x, 0);
    }

    [Fact]
    public void Deadzone_SuppressesSmallMotion()
    {
        var g = Gyro(s => { s.DeadzoneDps = 3; s.AutoCalibrate = false; });
        var m = g.Process(new Vector3(1, 1, 0), null, 0, 0.01, true);
        Assert.Equal(Vector2.Zero, m);
        var m2 = g.Process(new Vector3(0, -10, 0), null, 10, 0.01, true);
        Assert.True(m2.X > 0);
    }

    [Fact]
    public void Inactive_ProducesNothing()
    {
        var g = Gyro(s => s.AutoCalibrate = false);
        Assert.Equal(Vector2.Zero, g.Process(new Vector3(50, 50, 50), null, 0, 0.01, false));
    }

    [Fact]
    public void ManualCalibration_UsesAverage()
    {
        var g = Gyro(s => s.AutoCalibrate = false);
        g.StartManualCalibration(0, 100);
        for (int t = 0; t <= 100; t += 2) g.Process(new Vector3(2, 2, 2), null, t, 0.002, false);
        Assert.True(g.IsCalibrated);
        Assert.Equal(2f, g.Bias.X, 3);
    }

    [Fact]
    public void Accel_IncreasesFastMotion()
    {
        var slow = Gyro(s => s.AutoCalibrate = false);
        var fast = Gyro(s => { s.AutoCalibrate = false; s.Accel = new GyroAccel { Enabled = true, LowDps = 10, HighDps = 100, MaxMultiplier = 3 }; });
        var a = slow.Process(new Vector3(0, -200, 0), null, 0, 0.01, true);
        var b = fast.Process(new Vector3(0, -200, 0), null, 0, 0.01, true);
        Assert.Equal(a.X * 3, b.X, 3);
    }

    [Fact]
    public void Stick_DeadzoneAndCurve()
    {
        var s = new StickSettings { Deadzone = 0.2, OuterDeadzone = 0, Curve = 1 };
        Assert.Equal(Vector2.Zero, StickProcessor.Process(0.1f, 0.1f, s));
        var full = StickProcessor.Process(1, 0, s);
        Assert.Equal(1f, full.X, 3);
        var half = StickProcessor.Process(0.6f, 0, s);
        Assert.Equal(0.5f, half.X, 3);
        var curved = StickProcessor.Process(0.6f, 0, new StickSettings { Deadzone = 0.2, OuterDeadzone = 0, Curve = 2 });
        Assert.Equal(0.25f, curved.X, 3);
    }

    [Fact]
    public void Derive_AddsTriggersAndStickDirectionsWithHysteresis()
    {
        var p = new Profile();
        var s = new ControllerState { LT = 0.5f, RT = 1f, LX = 0.55f };
        var set = StickProcessor.Derive(s, p, InputSet.Empty);
        Assert.True(set.Has(LogicalInput.LT));
        Assert.False(set.Has(LogicalInput.LT2));
        Assert.True(set.Has(LogicalInput.RT2));
        Assert.True(set.Has(LogicalInput.LSRight));
        // 0.45 は押したままなら維持、新規なら押さない
        s.LX = 0.45f;
        Assert.True(StickProcessor.Derive(s, p, set).Has(LogicalInput.LSRight));
        Assert.False(StickProcessor.Derive(s, p, InputSet.Empty).Has(LogicalInput.LSRight));
    }
}
