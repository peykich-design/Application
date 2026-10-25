using PadMapper.Core.Devices;
using PadMapper.Core.Diagnostics;
using PadMapper.Core.Model;

namespace PadMapper.Core.Tests;

public class GuidedMapperTests
{
    private const string Pad = "hid-pad";
    private const string Vendor = "vendor";
    private double _t;

    private static HidSnapshot Snap(int x = 32768, int y = 32768, int z = 32768, int rz = 32768, int rx = 0, int? hat = null, params int[] buttons)
    {
        var s = new HidSnapshot { Hat = hat };
        s.Values[HidSnapshot.UsageX] = x;
        s.Values[HidSnapshot.UsageY] = y;
        s.Values[HidSnapshot.UsageZ] = z;
        s.Values[HidSnapshot.UsageRz] = rz;
        s.Values[HidSnapshot.UsageRx] = rx; // LT（静止時 0）
        foreach (var b in buttons) s.Buttons.Add(b);
        return s;
    }

    private void Feed(GuidedMapper m, HidSnapshot s, int frames = 3)
    {
        for (int i = 0; i < frames; i++)
        {
            _t += 10;
            m.FeedHid(Pad, s, _t);
        }
    }

    private static byte[] Vader(byte buttons7 = 0, byte lx = 0x7F, byte noise = 0)
    {
        var r = new byte[32];
        r[0] = 4; r[1] = 0xFE; r[7] = buttons7; r[17] = lx; r[19] = 0x7F; r[26] = noise;
        return r;
    }

    [Fact]
    public void HidPad_FullFlow_ProducesDefinition()
    {
        var steps = new List<SetupStep>
        {
            new("rest", SetupStepKind.Rest), new("LX", SetupStepKind.StickRight), new("RX", SetupStepKind.StickRight),
            new("LT", SetupStepKind.Trigger), new("A", SetupStepKind.Button), new("DPadUp", SetupStepKind.Button),
        };
        var m = new GuidedMapper(steps);
        Feed(m, Snap(), 2);
        _t += GuidedMapper.RestMs;
        Feed(m, Snap(), 1);
        Assert.Equal("LX", m.Current!.Target);

        // 左スティック右：X が増える（途中で Y が少し揺れても良い）
        Feed(m, Snap(x: 65535, y: 34000));
        Feed(m, Snap());
        Assert.Equal("RX", m.Current!.Target);

        // 右スティック右：Z が「減る」機種（反転）
        Feed(m, Snap(z: 0));
        Feed(m, Snap());
        Assert.Equal("LT", m.Current!.Target);

        // LT：Rx が 0 → 65535
        Feed(m, Snap(rx: 65535));
        Feed(m, Snap());
        Assert.Equal("A", m.Current!.Target);

        // A：スティックを揺らしながら押してもボタンだけを見る
        Feed(m, Snap(x: 40000, buttons: 2));
        Assert.True(m.IsWaitingRelease);
        Feed(m, Snap());
        Assert.Equal("DPadUp", m.Current!.Target);

        Feed(m, Snap(hat: 0));
        Feed(m, Snap());
        Assert.True(m.IsFinished);

        var def = m.BuildDefinition(Pad, new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x1234, UsagePage = 1, Usage = 5, Product = "Pad" }, null);
        Assert.True(def.IsHid);
        Assert.Equal(2, def.Buttons.Single(b => b.Input == "A").HidButton);
        Assert.Equal("up", def.Buttons.Single(b => b.Input == "DPadUp").HidHat);
        var rx = def.Axes.Single(a => a.Axis == "RX");
        Assert.Equal(HidSnapshot.UsageZ, rx.HidUsage);
        Assert.True(rx.Invert);
        var lt = def.Axes.Single(a => a.Axis == "LT");
        Assert.Equal(0, lt.Min);
        Assert.Equal(65535, lt.Max);

        // 作った定義で読むと、右に倒した右スティックが +1 になる
        var parser = new DeviceParser(def);
        var state = new ControllerState();
        parser.TryParseHid(Snap(z: 0, rx: 65535, buttons: 2), state);
        Assert.Equal(1f, state.RX, 2);
        Assert.Equal(1f, state.LT, 2);
        Assert.True(state.Buttons.Has(LogicalInput.A));
    }

    [Fact]
    public void Raw_StickJitterIsNotMistakenForButton()
    {
        var steps = new List<SetupStep>
        {
            new("rest", SetupStepKind.Rest), new("LX", SetupStepKind.StickRight), new("M1", SetupStepKind.Button),
        };
        var m = new GuidedMapper(steps);
        // 静止中もジャイロのバイト（26）は揺れている
        for (int i = 0; i < 20; i++) m.FeedRaw(Vendor, Vader(noise: (byte)(i % 5)), _t += 60);
        Assert.Equal("LX", m.Current!.Target);

        // スティックを右へ倒して戻す（途中の値をたくさん通る）
        for (int v = 0x7F; v <= 0xFF; v += 8) m.FeedRaw(Vendor, Vader(lx: (byte)v), _t += 5);
        for (int v = 0xFF; v >= 0x7F; v -= 8) m.FeedRaw(Vendor, Vader(lx: (byte)v), _t += 5);
        m.FeedRaw(Vendor, Vader(), _t += 5);
        Assert.Equal("M1", m.Current!.Target);

        // スティックが 1 だけ揺れてもボタンにはならない
        m.FeedRaw(Vendor, Vader(lx: 0x7E), _t += 10);
        m.FeedRaw(Vendor, Vader(lx: 0x7F), _t += 10);
        Assert.Equal("M1", m.Current!.Target);
        Assert.False(m.IsWaitingRelease);

        // M1 を押して離す
        m.FeedRaw(Vendor, Vader(buttons7: 0x04), _t += 30);
        m.FeedRaw(Vendor, Vader(buttons7: 0x04), _t += 30);
        m.FeedRaw(Vendor, Vader(), _t += 10);
        Assert.True(m.IsFinished);

        var def = m.BuildDefinition(Vendor, new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x2412, UsagePage = 0xFFA0, Usage = 1 },
            BuiltInDevices.Vader4ProV1());
        Assert.False(def.IsHid);
        var m1 = def.Buttons.Single(b => b.Input == "M1");
        Assert.Equal(7, m1.Offset);
        Assert.Equal(0x04, m1.Mask);
        Assert.Equal(17, def.Axes.Single(a => a.Axis == "LX").Offset);
        // 元の定義のジャイロは残る
        Assert.NotEmpty(def.Gyro);
        Assert.Equal(4, def.ReportId);
    }

    [Fact]
    public void HidButtonIsPreferredOverVendorBit()
    {
        var m = new GuidedMapper(new List<SetupStep> { new("rest", SetupStepKind.Rest), new("A", SetupStepKind.Button) });
        m.FeedHid(Pad, Snap(), _t += 10);
        m.FeedRaw(Vendor, Vader(), _t += 10);
        _t += GuidedMapper.RestMs;
        m.Tick(_t);
        Assert.Equal("A", m.Current!.Target);
        // 同じボタンが両方のレポートに出る
        m.FeedRaw(Vendor, Vader(buttons7: 0x40), _t += 1);
        m.FeedHid(Pad, Snap(buttons: 1), _t += 1);
        m.FeedRaw(Vendor, Vader(), _t += 30);
        m.FeedHid(Pad, Snap(), _t += 1);
        Assert.True(m.IsFinished);
        Assert.Equal(Pad, m.Assignments.Single().Source);
    }

    [Fact]
    public void Trigger_FallsBackToDigitalButton()
    {
        var m = new GuidedMapper(new List<SetupStep> { new("rest", SetupStepKind.Rest), new("LT", SetupStepKind.Trigger) });
        Feed(m, Snap(), 2);
        _t += GuidedMapper.RestMs;
        Feed(m, Snap(), 1);
        Feed(m, Snap(buttons: 7));
        Feed(m, Snap());
        Assert.True(m.IsFinished);
        var def = m.BuildDefinition(Pad, new HidDeviceInfo { UsagePage = 1, Usage = 5 }, null);
        Assert.Equal(7, def.Buttons.Single(b => b.Input == "LT").HidButton);
    }

    [Fact]
    public void SkipAndBack()
    {
        var m = new GuidedMapper();
        m.Skip(); // rest
        Assert.Equal("LX", m.Current!.Target);
        m.Skip();
        Assert.Equal("LY", m.Current!.Target);
        m.Back();
        Assert.Equal("LX", m.Current!.Target);
        m.Restart();
        Assert.Equal(0, m.Index);
    }

    [Fact]
    public void GenericDefinition_ReadsButtonsHatAndLeftStick()
    {
        var parser = new DeviceParser(BuiltInDevices.GenericGamepad(5));
        var s = new ControllerState();
        Assert.True(parser.TryParseHid(Snap(x: 65535, y: 0, hat: 1, buttons: new[] { 1, 6 }), s));
        Assert.True(s.Buttons.Has(LogicalInput.A));
        Assert.True(s.Buttons.Has(LogicalInput.RB));
        Assert.True(s.Buttons.Has(LogicalInput.DPadUp));
        Assert.True(s.Buttons.Has(LogicalInput.DPadRight));
        Assert.Equal(1f, s.LX, 2);
        Assert.Equal(-1f, s.LY, 2);
        // 右スティック・トリガーは仮の定義では読まない（誤動作防止）
        Assert.Equal(0f, s.RX);
        Assert.Equal(0f, s.LT);
        // 生レポートとしては読まない
        Assert.False(parser.TryParse(new byte[32], s));
    }

    [Fact]
    public void Merger_CombinesCollections()
    {
        var hid = new DeviceParser(BuiltInDevices.GenericGamepad(5));
        var vendor = new DeviceParser(BuiltInDevices.Vader4ProV1());
        var a = new ControllerState { Buttons = InputSet.Of(LogicalInput.A), LX = 0.5f };
        var b = new ControllerState { Buttons = InputSet.Of(LogicalInput.M1), LX = -1f, RX = 0.3f, HasGyro = true, Gyro = new(1, 2, 3) };
        var m = StateMerger.Merge(new[] { (hid, a), (vendor, b) }, 0);
        Assert.True(m.Buttons.Has(LogicalInput.A) && m.Buttons.Has(LogicalInput.M1));
        Assert.Equal(-1f, m.LX); // 両方が LX を持つときは後の値
        Assert.Equal(0.3f, m.RX);
        Assert.True(m.HasGyro);
    }

    [Fact]
    public void FlydigiMatch_IsVendorCollectionOnly()
    {
        var def = BuiltInDevices.Vader4ProV1();
        Assert.True(def.Match.Matches(new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x9999, UsagePage = 0xFFA0, Usage = 1 }));
        Assert.False(def.Match.Matches(new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x2412, UsagePage = 1, Usage = 5 }));
        Assert.True(BuiltInDevices.GenericGamepad(5).Match.Matches(new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x2412, UsagePage = 1, Usage = 5 }));
    }

    [Fact]
    public void HidRange_NormalizesTo16Bit()
    {
        Assert.Equal(0, new HidRange(-127, 127).Normalize(-127));
        Assert.Equal(65535, new HidRange(-127, 127).Normalize(127));
        Assert.InRange(new HidRange(0, 255).Normalize(128), 32700, 32900);
    }
}

public class GuidedMapperTriggerTests
{
    [Fact]
    public void Trigger_PrefersAxisOverDigitalBitThatFiresFirst()
    {
        double t = 0;
        var m = new GuidedMapper(new List<SetupStep> { new("rest", SetupStepKind.Rest), new("LT", SetupStepKind.Trigger) });
        HidSnapshot S(int rx, params int[] b)
        {
            var s = new HidSnapshot();
            s.Values[HidSnapshot.UsageRx] = rx;
            foreach (var x in b) s.Buttons.Add(x);
            return s;
        }
        m.FeedHid("p", S(0), t += 10);
        t += GuidedMapper.RestMs;
        m.FeedHid("p", S(0), t += 10);
        m.FeedHid("p", S(5000, 7), t += 10);   // デジタルが先に反応
        m.FeedHid("p", S(65535, 7), t += 10);  // 軸が奥まで
        m.FeedHid("p", S(0), t += 10);
        Assert.True(m.IsFinished);
        Assert.Equal("axis:" + HidSnapshot.UsageRx, m.Assignments.Single().Signal);
    }
}
