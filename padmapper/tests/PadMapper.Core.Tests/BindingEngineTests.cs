using PadMapper.Core.Engine;
using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Tests;

public class BindingEngineTests
{
    private readonly OutputTracker _out = new();
    private readonly BindingEngine _engine;
    private readonly List<EngineRequest> _requests = new();

    public BindingEngineTests()
    {
        _engine = new BindingEngine(_out);
        _engine.Request += r => _requests.Add(r);
    }

    private void Load(ProfileType type, params Binding[] bindings)
    {
        var p = new Profile { Id = "t", Type = type, Overlay = new OverlaySettings { Trigger = null } };
        p.Bindings.AddRange(bindings);
        _engine.Load(p);
    }

    private List<string> Step(InputSet inputs, double t)
    {
        _engine.Update(inputs, t);
        return _out.Drain().Select(c => c.ToString()).ToList();
    }

    private static InputSet I(params LogicalInput[] i) => InputSet.Of(i);

    [Fact]
    public void Press_HoldsKeyWhilePressed()
    {
        Load(ProfileType.Desktop, new Binding { Input = "A", Action = ActionDef.KeyPress("Space") });
        Assert.Equal(new[] { "KeyDown(Space)" }, Step(I(LogicalInput.A), 0));
        Assert.Empty(Step(I(LogicalInput.A), 10));
        Assert.Equal(new[] { "KeyUp(Space)" }, Step(InputSet.Empty, 20));
    }

    [Fact]
    public void Press_WithModifiers_ReleasesInReverseOrder()
    {
        Load(ProfileType.Desktop, new Binding { Input = "A", Action = ActionDef.KeyPress("C", "Ctrl", "Shift") });
        Assert.Equal(new[] { "KeyDown(LCtrl)", "KeyDown(LShift)", "KeyDown(C)" }, Step(I(LogicalInput.A), 0));
        Assert.Equal(new[] { "KeyUp(C)", "KeyUp(LShift)", "KeyUp(LCtrl)" }, Step(InputSet.Empty, 10));
    }

    [Fact]
    public void SameKeyFromTwoBindings_IsReleasedOnlyAfterBoth()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "A", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "B", Action = ActionDef.KeyPress("Space") });
        Assert.Equal(new[] { "KeyDown(Space)" }, Step(I(LogicalInput.A), 0));
        Assert.Empty(Step(I(LogicalInput.A, LogicalInput.B), 10));
        Assert.Empty(Step(I(LogicalInput.B), 20));
        Assert.Equal(new[] { "KeyUp(Space)" }, Step(InputSet.Empty, 30));
    }

    [Fact]
    public void Release_FiresOnRelease()
    {
        Load(ProfileType.Desktop, new Binding { Input = "A", Trigger = TriggerKind.Release, Action = ActionDef.KeyPress("Enter") });
        Assert.Empty(Step(I(LogicalInput.A), 0));
        Assert.Equal(new[] { "KeyDown(Enter)" }, Step(InputSet.Empty, 100));
        Assert.Equal(new[] { "KeyUp(Enter)" }, Step(InputSet.Empty, 100 + MacroParser.TapMs));
    }

    [Fact]
    public void TapAndHold_OnSameButton_AreExclusive()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "X", Trigger = TriggerKind.Tap, HoldMs = 300, Action = ActionDef.KeyPress("T") },
            new Binding { Input = "X", Trigger = TriggerKind.Hold, HoldMs = 300, Action = ActionDef.KeyPress("H") });

        // 短押し → T だけ
        Step(I(LogicalInput.X), 0);
        Assert.Equal(new[] { "KeyDown(T)" }, Step(InputSet.Empty, 100));
        Assert.Equal(new[] { "KeyUp(T)" }, Step(InputSet.Empty, 200));

        // 長押し → H を押しっぱなし、離しても T は出ない
        Assert.Empty(Step(I(LogicalInput.X), 1000));
        Assert.Empty(Step(I(LogicalInput.X), 1299));
        Assert.Equal(new[] { "KeyDown(H)" }, Step(I(LogicalInput.X), 1300));
        Assert.Equal(new[] { "KeyUp(H)" }, Step(InputSet.Empty, 1500));
    }

    [Fact]
    public void Hold_ReportsDeadline()
    {
        Load(ProfileType.Desktop, new Binding { Input = "M2", Trigger = TriggerKind.Hold, HoldMs = 500, Action = ActionDef.KeyPress("F1") });
        Step(I(LogicalInput.M2), 1000);
        Assert.Equal(1500, _engine.NextDeadline());
        Step(I(LogicalInput.M2), 1500);
        Assert.Null(_engine.NextDeadline());
    }

    [Fact]
    public void Double_FiresOnSecondPressWithinWindow()
    {
        Load(ProfileType.Desktop, new Binding { Input = "C", Trigger = TriggerKind.Double, DoubleMs = 300, Action = ActionDef.Mouse(MouseButton.Middle) });
        Assert.Empty(Step(I(LogicalInput.C), 0));
        Assert.Empty(Step(InputSet.Empty, 50));
        Assert.Equal(new[] { "MouseDown(Middle)" }, Step(I(LogicalInput.C), 200));
        Assert.Equal(new[] { "MouseUp(Middle)" }, Step(InputSet.Empty, 250));
        // 3回目はすぐには発火しない（新しい1回目になる）
        Assert.Empty(Step(I(LogicalInput.C), 300));
    }

    [Fact]
    public void Double_TooSlow_DoesNotFire()
    {
        Load(ProfileType.Desktop, new Binding { Input = "C", Trigger = TriggerKind.Double, DoubleMs = 300, Action = ActionDef.KeyPress("D") });
        Step(I(LogicalInput.C), 0);
        Step(InputSet.Empty, 50);
        Assert.Empty(Step(I(LogicalInput.C), 400));
    }

    [Fact]
    public void Chord_RequiresAllInputs()
    {
        Load(ProfileType.Desktop, new Binding
        {
            Input = "LB", Trigger = TriggerKind.Chord, Chord = new() { "RB" }, Action = ActionDef.KeyPress("Tab", "Alt"),
        });
        Assert.Empty(Step(I(LogicalInput.LB), 0));
        Assert.Equal(new[] { "KeyDown(LAlt)", "KeyDown(Tab)" }, Step(I(LogicalInput.LB, LogicalInput.RB), 10));
        Assert.Equal(new[] { "KeyUp(Tab)", "KeyUp(LAlt)" }, Step(I(LogicalInput.RB), 20));
    }

    [Fact]
    public void ModeShift_SwitchesLayerWhileHeld()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "LB", Trigger = TriggerKind.ModeShift, Layer = "shift" },
            new Binding { Input = "A", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "A", Layer = "shift", Action = ActionDef.KeyPress("Enter") },
            new Binding { Input = "B", Action = ActionDef.KeyPress("Esc") });

        Assert.Empty(Step(I(LogicalInput.LB), 0));
        Assert.Equal("shift", _engine.ActiveLayer);
        Assert.Equal(new[] { "KeyDown(Enter)" }, Step(I(LogicalInput.LB, LogicalInput.A), 10));
        Assert.Equal(new[] { "KeyUp(Enter)" }, Step(I(LogicalInput.LB), 20));
        // レイヤーに割り当てのない B は基本レイヤーのまま
        Assert.Equal(new[] { "KeyDown(Esc)" }, Step(I(LogicalInput.LB, LogicalInput.B), 30));
        Step(InputSet.Empty, 40);
        Assert.Null(_engine.ActiveLayer);
        Assert.Equal(new[] { "KeyDown(Space)" }, Step(I(LogicalInput.A), 50));
    }

    [Fact]
    public void ModeShift_IsIgnoredInGameProfiles()
    {
        Load(ProfileType.Game,
            new Binding { Input = "LB", Trigger = TriggerKind.ModeShift, Layer = "shift" },
            new Binding { Input = "M1", Layer = "shift", Action = ActionDef.KeyPress("Enter") });
        Step(I(LogicalInput.LB), 0);
        Assert.Null(_engine.ActiveLayer);
        Assert.Empty(Step(I(LogicalInput.LB, LogicalInput.M1), 10));
    }

    [Fact]
    public void ProfileAction_RaisesRequest()
    {
        Load(ProfileType.Game, new Binding { Input = "M2", Trigger = TriggerKind.Hold, HoldMs = 500, Action = new ActionDef { Type = ActionType.Profile, Id = "desktop" } });
        Step(I(LogicalInput.M2), 0);
        Step(I(LogicalInput.M2), 600);
        Assert.Single(_requests);
        Assert.Equal(EngineRequestKind.SwitchProfile, _requests[0].Kind);
        Assert.Equal("desktop", _requests[0].ProfileId);
    }

    [Fact]
    public void Paused_BlocksOutputButNotPauseAction()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "A", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "Guide", Action = new ActionDef { Type = ActionType.Pause } });
        _engine.Paused = true;
        Assert.Empty(Step(I(LogicalInput.A), 0));
        Step(I(LogicalInput.A, LogicalInput.Guide), 10);
        Assert.Contains(_requests, r => r.Kind == EngineRequestKind.TogglePause);
    }

    [Fact]
    public void ReleaseAll_ReleasesHeldAndDoesNotRetrigger()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "A", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "RT", Action = ActionDef.Mouse(MouseButton.Left) });
        Step(I(LogicalInput.A, LogicalInput.RT), 0);
        _engine.ReleaseAll();
        var released = _out.Drain().Select(c => c.ToString()).ToList();
        Assert.Contains("KeyUp(Space)", released);
        Assert.Contains("MouseUp(Left)", released);
        // 押しっぱなしのままでも再び押下にはならない
        Assert.Empty(Step(I(LogicalInput.A, LogicalInput.RT), 10));
        Assert.Empty(Step(InputSet.Empty, 20));
    }

    [Fact]
    public void Load_WhileHolding_DoesNotFireHoldImmediately()
    {
        var hold = new Binding { Input = "M2", Trigger = TriggerKind.Hold, HoldMs = 500, Action = new ActionDef { Type = ActionType.Profile, Id = "x" } };
        Load(ProfileType.Game, hold);
        Step(I(LogicalInput.M2), 0);
        Step(I(LogicalInput.M2), 600);
        Assert.Single(_requests);
        // プロファイル切替（同じボタンに長押しがある）
        Load(ProfileType.Desktop, hold);
        Step(I(LogicalInput.M2), 1200);
        Assert.Single(_requests);
        Step(InputSet.Empty, 1300);
        Step(I(LogicalInput.M2), 1400);
        Step(I(LogicalInput.M2), 1900);
        Assert.Equal(2, _requests.Count);
    }

    [Fact]
    public void Macro_RunsStepsWithDelays()
    {
        Load(ProfileType.Desktop, new Binding
        {
            Input = "Y", Action = new ActionDef { Type = ActionType.Macro, Macro = "Ctrl+C; wait 100; tap V" },
        });
        Assert.Equal(new[] { "KeyDown(LCtrl)", "KeyDown(C)" }, Step(I(LogicalInput.Y), 0));
        Assert.Equal(new[] { "KeyUp(C)", "KeyUp(LCtrl)" }, Step(I(LogicalInput.Y), 20));
        Assert.Empty(Step(InputSet.Empty, 50));
        Assert.Equal(120, _engine.NextDeadline());
        Assert.Equal(new[] { "KeyDown(V)" }, Step(InputSet.Empty, 120));
        Assert.Equal(new[] { "KeyUp(V)" }, Step(InputSet.Empty, 140));
    }

    [Fact]
    public void Overlay_TriggerFromProfileRaisesRequest()
    {
        var p = new Profile { Id = "t", Type = ProfileType.Game };
        _engine.Load(p);
        Step(I(LogicalInput.M4), 0);
        Step(I(LogicalInput.M4), 700);
        Assert.Contains(_requests, r => r.Kind == EngineRequestKind.OpenOverlay);
    }

    [Fact]
    public void Scroll_And_MouseMove_AreOneShot()
    {
        Load(ProfileType.Desktop,
            new Binding { Input = "DPadUp", Action = new ActionDef { Type = ActionType.Scroll, Amount = 1 } },
            new Binding { Input = "DPadRight", Action = new ActionDef { Type = ActionType.MouseMove, Dx = 5, Dy = -3 } });
        Assert.Equal(new[] { "Scroll(120,V)" }, Step(I(LogicalInput.DPadUp), 0));
        Assert.Empty(Step(I(LogicalInput.DPadUp), 10));
        Assert.Equal(new[] { "Move(5,-3)" }, Step(I(LogicalInput.DPadRight), 20));
    }
}
