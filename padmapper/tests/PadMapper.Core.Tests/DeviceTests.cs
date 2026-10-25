using PadMapper.Core.Devices;
using PadMapper.Core.Diagnostics;
using PadMapper.Core.Model;

namespace PadMapper.Core.Tests;

public class DeviceTests
{
    /// <summary>SDL の V1 レイアウトに沿った、静止状態の Vader 4 Pro レポート。</summary>
    private static byte[] VaderIdle()
    {
        var r = new byte[32];
        r[0] = 0x04; r[1] = 0xFE;
        r[17] = 0x7F; r[19] = 0x7F; r[21] = 0x7F; r[22] = 0x7F;
        return r;
    }

    [Fact]
    public void Vader_ParsesButtonsAxesAndGyro()
    {
        var parser = new DeviceParser(BuiltInDevices.Vader4ProV1());
        var r = VaderIdle();
        r[7] = 0x04 | 0x20 | 0x01;      // M1, M4, C
        r[9] = 0x10 | 0x01;             // A, 十字キー上
        r[10] = 0x04;                   // LB
        r[17] = 0xFF;                   // LX 右いっぱい
        r[19] = 0x00;                   // LY 上いっぱい
        r[23] = 0xFF;                   // LT
        // ヨー（下位 18、上位 20）= 100
        r[18] = 100; r[20] = 0;
        var s = new ControllerState();
        Assert.True(parser.TryParse(r, s));
        Assert.True(s.Buttons.Has(LogicalInput.M1));
        Assert.True(s.Buttons.Has(LogicalInput.M4));
        Assert.True(s.Buttons.Has(LogicalInput.C));
        Assert.False(s.Buttons.Has(LogicalInput.M2));
        Assert.True(s.Buttons.Has(LogicalInput.A));
        Assert.True(s.Buttons.Has(LogicalInput.DPadUp));
        Assert.True(s.Buttons.Has(LogicalInput.LB));
        Assert.Equal(1f, s.LX, 3);
        Assert.Equal(-1f, s.LY, 3);
        Assert.Equal(0f, s.RX, 3);
        Assert.Equal(1f, s.LT, 3);
        Assert.True(s.HasGyro);
        Assert.Equal(-100 * 72000.0 / 32767.0, s.Gyro.Y, 1);
    }

    [Fact]
    public void Vader_RejectsOtherReports()
    {
        var parser = new DeviceParser(BuiltInDevices.Vader4ProV1());
        var s = new ControllerState();
        var r = VaderIdle();
        r[1] = 0x00;
        Assert.False(parser.TryParse(r, s));
        Assert.False(parser.TryParse(new byte[10], s));
    }

    [Fact]
    public void DeviceMatch_RequiresAtLeastOneCondition()
    {
        var d = new HidDeviceInfo { VendorId = 0x04B4, ProductId = 0x2412, UsagePage = 0xFFA0, Usage = 1, Product = "Flydigi VADER4" };
        Assert.True(new DeviceMatch { VendorId = 0x04B4 }.Matches(d));
        Assert.True(new DeviceMatch { ProductContains = "vader" }.Matches(d));
        Assert.False(new DeviceMatch { VendorId = 0x054C }.Matches(d));
        Assert.False(new DeviceMatch().Matches(d));
    }

    [Fact]
    public void Connection_IsGuessedFromPath()
    {
        Assert.Equal("Bluetooth", HidDeviceInfo.GuessConnection(@"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c"));
        Assert.Equal("USB", HidDeviceInfo.GuessConnection(@"\\?\HID#VID_04B4&PID_2412&MI_02#8&1"));
    }

    [Fact]
    public void Learner_FindsSingleBitButton()
    {
        var l = new MappingLearner();
        for (int i = 0; i < 20; i++)
        {
            var r = VaderIdle();
            r[18] = (byte)(i % 3); // 揺れているバイト（ジャイロのノイズ）
            l.AddIdle(r);
        }
        for (int i = 0; i < 20; i++)
        {
            var r = VaderIdle();
            r[18] = (byte)(i % 3);
            if (i is > 5 and < 15) r[7] |= 0x08; // M2
            l.AddActive(r);
        }
        var b = l.LearnButton("M2");
        Assert.NotNull(b);
        Assert.Equal(7, b!.Offset);
        Assert.Equal(0x08, b.Mask);
        Assert.Null(b.Value);
    }

    [Fact]
    public void Learner_UsesValueForHatSwitch()
    {
        var l = new MappingLearner();
        for (int i = 0; i < 5; i++)
        {
            var r = new byte[8];
            r[3] = 0x0F; // ハット中立
            l.AddIdle(r);
        }
        for (int i = 0; i < 10; i++)
        {
            var r = new byte[8];
            r[3] = 0x02; // 右
            l.AddActive(r);
        }
        var b = l.LearnButton("DPadRight");
        Assert.NotNull(b);
        Assert.Equal(3, b!.Offset);
        Assert.Equal(0x02, b.Value);
        // 判定が正しく働くこと
        var def = new DeviceDefinition { Buttons = { b } };
        var s = new ControllerState();
        var rr = new byte[8];
        rr[3] = 0x02;
        Assert.True(new DeviceParser(def).TryParse(rr, s));
        Assert.True(s.Buttons.Has(LogicalInput.DPadRight));
        rr[3] = 0x0F;
        new DeviceParser(def).TryParse(rr, s);
        Assert.False(s.Buttons.Has(LogicalInput.DPadRight));
    }

    [Fact]
    public void Learner_FindsStickAxis()
    {
        var l = new MappingLearner();
        for (int i = 0; i < 10; i++) l.AddIdle(VaderIdle());
        for (int v = 0; v <= 255; v += 5)
        {
            var r = VaderIdle();
            r[21] = (byte)v;
            l.AddActive(r);
        }
        var a = l.LearnAxis("RX");
        Assert.NotNull(a);
        Assert.Equal(21, a!.Offset);
        Assert.Equal(127, a.Center);
    }

    [Fact]
    public void Learner_FindsGyroPair()
    {
        var l = new MappingLearner();
        for (int i = 0; i < 10; i++) l.AddIdle(VaderIdle());
        for (int i = 0; i < 40; i++)
        {
            var r = VaderIdle();
            short v = (short)(Math.Sin(i / 3.0) * 3000);
            r[26] = (byte)(v & 0xFF);
            r[27] = (byte)((v >> 8) & 0xFF);
            l.AddActive(r);
        }
        var g = l.LearnSensor("pitch", 0.061);
        Assert.NotNull(g);
        Assert.Equal(26, g!.Offset);
    }

    [Fact]
    public void ReportDiff_ShowsChangedBits()
    {
        var bits = ReportDiff.ChangedBitList(new byte[] { 0x00, 0xF0 }, new byte[] { 0x01, 0xF0 }).ToList();
        Assert.Equal(new[] { (0, 0) }, bits);
        Assert.Equal("04 FE", ReportDiff.ToHex(new byte[] { 4, 0xFE }));
        Assert.Equal(new byte[] { 0x05, 0xEC }, ReportDiff.FromHex("05 ec"));
    }

    [Fact]
    public void Markdown_ListsMissingItems()
    {
        var def = new DeviceDefinition { Name = "Test", Buttons = { new ButtonMap { Input = "A", Offset = 1, Mask = 1 } } };
        var md = ReportMarkdown.Build(def, null, null);
        Assert.Contains("| A | 1 | 0x01 |", md);
        Assert.Contains("M1", md);
        Assert.Contains("未特定", md);
        Assert.DoesNotContain("A、", string.Join("、", ReportMarkdown.Missing(def)) + "、");
    }

    [Fact]
    public void BuiltInVader_IsMarkedUnverified()
    {
        var def = BuiltInDevices.Vader4ProV1();
        Assert.False(def.Verified);
        Assert.Empty(ReportMarkdown.Missing(def));
    }
}
