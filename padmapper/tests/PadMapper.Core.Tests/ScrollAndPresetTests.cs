using System.Numerics;
using PadMapper.Core.Engine;
using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Tests;

public class ScrollAndPresetTests
{
    private static readonly StickSettings Scroll = new()
    {
        Mode = StickMode.Scroll, Speed = 30, MinSpeed = 2, Curve = 1, Deadzone = 0, OuterDeadzone = 0,
    };

    [Fact]
    public void ScrollRate_GrowsWithTilt()
    {
        var slow = StickProcessor.ScrollRate(StickProcessor.Process(0, -0.1f, Scroll), Scroll);
        var mid = StickProcessor.ScrollRate(StickProcessor.Process(0, -0.5f, Scroll), Scroll);
        var full = StickProcessor.ScrollRate(StickProcessor.Process(0, -1f, Scroll), Scroll);
        Assert.InRange(slow.Y, 2, 5);        // 少し倒すとゆっくり（上に倒すと正）
        Assert.Equal(16, mid.Y, 1);          // 2 + (30-2)*0.5
        Assert.Equal(30, full.Y, 1);
        Assert.True(slow.Y < mid.Y && mid.Y < full.Y);
    }

    [Fact]
    public void ScrollRate_DropsSmallCrossAxis()
    {
        var r = StickProcessor.ScrollRate(new Vector2(0.2f, 0.9f), Scroll);
        Assert.Equal(0, r.X);
        Assert.True(r.Y < 0); // 下に倒すと下
        var diag = StickProcessor.ScrollRate(new Vector2(0.7f, 0.7f), Scroll);
        Assert.True(diag.X > 0 && diag.Y < 0);
    }

    [Fact]
    public void Accumulator_StepModes()
    {
        var smooth = new MouseAccumulator();
        Assert.Equal(12, smooth.AddScroll(0.1, false, 1));
        var notch = new MouseAccumulator();
        Assert.Equal(0, notch.AddScroll(0.5, false, 120));
        Assert.Equal(120, notch.AddScroll(0.6, false, 120));
        Assert.Equal(-120, notch.AddScroll(-1.2, false, 120)); // 端数 0.1 が残っていても下へ 1 ノッチ
    }

    [Fact]
    public void Engine_SmoothScroll_SendsSmallSteadySteps()
    {
        double now = 0;
        var sink = new RecordingSink();
        var engine = new PadEngine(sink, clock: () => now);
        var p = new Profile { Id = "d", Type = ProfileType.Desktop };
        p.Sticks.Right = new StickSettings { Mode = StickMode.Scroll, Speed = 30, MinSpeed = 1, Curve = 1, Deadzone = 0, OuterDeadzone = 0 };
        engine.SetProfile(p);
        engine.Pump();
        var s = new ControllerState { RY = -1 };
        bool moving = false;
        engine.ContinuousOutputChanged += m => moving = m;
        for (int i = 0; i <= 25; i++)
        {
            now = i * 4;
            engine.PostState(s);
            engine.Pump();
        }
        Assert.True(moving);
        var wheels = sink.Commands.Where(c => c.Kind == OutputKind.Scroll).Select(c => c.Wheel).ToList();
        Assert.True(wheels.Count >= 20, $"steps {wheels.Count}");
        Assert.All(wheels, w => Assert.InRange(w, 1, 30)); // 1 ノッチ（120）より細かい
        Assert.InRange(wheels.Sum(), 340, 361);             // 0.1 秒で約 3 ノッチ（360）
        now = 200;
        engine.PostState(new ControllerState());
        engine.Pump();
        Assert.False(moving);
    }

    [Fact]
    public void Presets_UseKnownKeys_AndAreUnique()
    {
        Assert.Equal(ShortcutPresets.All.Count, ShortcutPresets.All.Select(p => p.Id).Distinct().Count());
        foreach (var p in ShortcutPresets.All)
        {
            Assert.True(KeyCatalog.TryGet(p.Key, out _), p.Id);
            Assert.All(p.Modifiers, m => Assert.True(KeyCatalog.TryGet(m, out _), p.Id));
        }
    }

    [Fact]
    public void Presets_FindByKeyAndModifiers()
    {
        Assert.Equal("NextTab", ShortcutPresets.Find("Tab", new[] { "Ctrl" })!.Id);
        Assert.Equal("PrevTab", ShortcutPresets.Find("Tab", new[] { "LShift", "LCtrl" })!.Id);
        Assert.Null(ShortcutPresets.Find("Tab", new[] { "LShift" }));
    }

    [Fact]
    public void DesktopDefault_TabsOnBumpers()
    {
        var d = DefaultProfiles.Desktop();
        var rb = d.Bindings.First(b => b.Input == "RB" && b.Trigger == TriggerKind.Tap);
        Assert.Equal("NextTab", ShortcutPresets.Find(rb.Action.Key, rb.Action.Modifiers)!.Id);
    }
}
