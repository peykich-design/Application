using PadMapper.Core.Engine;
using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Tests;

/// <summary>スレッドを使わず、時計を差し替えて Pump で1手ずつ動かす。</summary>
public class PadEngineTests
{
    private double _now;
    private readonly RecordingSink _sink = new();
    private readonly PadEngine _engine;

    public PadEngineTests()
    {
        _engine = new PadEngine(_sink, clock: () => _now);
    }

    private void Run(ControllerState s, double t)
    {
        _now = t;
        _engine.PostState(s);
        _engine.Pump();
    }

    private void Apply(Profile p)
    {
        _engine.SetProfile(p);
        _engine.Pump();
    }

    [Fact]
    public void StickMouse_MovesCursorOverTime()
    {
        Apply(new Profile
        {
            Id = "d", Type = ProfileType.Desktop,
            Sticks = new SticksSettings { Left = new StickSettings { Mode = StickMode.Mouse, Speed = 1000, Deadzone = 0, OuterDeadzone = 0, Curve = 1 } },
        });
        var s = new ControllerState { LX = 1 };
        Run(s, 0);
        Run(s, 10);
        Run(s, 20);
        int dx = _sink.Commands.Where(c => c.Kind == OutputKind.MouseMove).Sum(c => c.Dx);
        Assert.Equal(20, dx);
    }

    [Fact]
    public void StickMouse_IsDisabledInGameProfile()
    {
        Apply(new Profile
        {
            Id = "g", Type = ProfileType.Game,
            Sticks = new SticksSettings { Left = new StickSettings { Mode = StickMode.Mouse, Speed = 1000, Deadzone = 0 } },
        });
        Run(new ControllerState { LX = 1 }, 0);
        Run(new ControllerState { LX = 1 }, 10);
        Assert.DoesNotContain(_sink.Commands, c => c.Kind == OutputKind.MouseMove);
    }

    [Fact]
    public void ProfileSwitch_ReleasesHeldKeys()
    {
        var p = new Profile { Id = "a", Bindings = { new Binding { Input = "A", Action = ActionDef.KeyPress("Space") } } };
        Apply(p);
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.A) }, 0);
        Assert.Contains(OutputCommand.KeyDown("Space"), _sink.Commands);
        Apply(new Profile { Id = "b" });
        Assert.Contains(OutputCommand.KeyUp("Space"), _sink.Commands);
    }

    [Fact]
    public void Pause_ReleasesAndBlocks()
    {
        Apply(new Profile { Id = "a", Bindings = { new Binding { Input = "A", Action = ActionDef.KeyPress("Space") } } });
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.A) }, 0);
        bool? paused = null;
        _engine.PausedChanged += p => paused = p;
        _engine.SetPaused(true);
        _engine.Pump();
        Assert.True(paused);
        Assert.Contains(OutputCommand.KeyUp("Space"), _sink.Commands);
        _sink.Clear();
        Run(new ControllerState(), 10);
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.A) }, 20);
        Assert.Empty(_sink.Commands);
    }

    [Fact]
    public void Disconnect_ReleasesEverything()
    {
        Apply(new Profile { Id = "a", Bindings = { new Binding { Input = "RT", Action = ActionDef.Mouse(MouseButton.Left) } } });
        Run(new ControllerState { RT = 1 }, 0);
        Assert.Contains(OutputCommand.MouseDown(MouseButton.Left), _sink.Commands);
        _engine.DeviceDisconnected();
        _engine.Pump();
        Assert.Contains(OutputCommand.MouseUp(MouseButton.Left), _sink.Commands);
        Assert.False(_engine.Snapshot.Connected);
    }

    [Fact]
    public void Gyro_WhileHeld_OnlyMovesWhenHeld()
    {
        Apply(new Profile
        {
            Id = "g", Type = ProfileType.Game,
            Gyro = new GyroSettings
            {
                Enabled = true, Smoothing = 0, DeadzoneDps = 0, AutoCalibrate = false,
                Activation = new GyroActivation { Mode = GyroActivationMode.WhileHeld, Input = "LT" },
            },
        });
        var still = new ControllerState { HasGyro = true, Gyro = new(0, -100, 0) };
        Run(still, 0);
        Run(still, 10);
        Assert.DoesNotContain(_sink.Commands, c => c.Kind == OutputKind.MouseMove);
        var aiming = new ControllerState { HasGyro = true, Gyro = new(0, -100, 0), LT = 1 };
        Run(aiming, 20);
        Run(aiming, 30);
        Assert.Contains(_sink.Commands, c => c.Kind == OutputKind.MouseMove && c.Dx > 0);
    }

    [Fact]
    public void Gyro_Toggle_FlipsOnPress()
    {
        Apply(new Profile
        {
            Id = "g", Type = ProfileType.Game,
            Gyro = new GyroSettings
            {
                Enabled = true, Smoothing = 0, DeadzoneDps = 0, AutoCalibrate = false,
                Activation = new GyroActivation { Mode = GyroActivationMode.Toggle, Input = "M3" },
            },
        });
        ControllerState S(bool m3) => new() { HasGyro = true, Gyro = new(0, -100, 0), Buttons = m3 ? InputSet.Of(LogicalInput.M3) : InputSet.Empty };
        Run(S(false), 0);
        Run(S(true), 10);
        Run(S(false), 20);
        Run(S(false), 30);
        Assert.Contains(_sink.Commands, c => c.Kind == OutputKind.MouseMove);
        _sink.Clear();
        Run(S(true), 40);
        Run(S(false), 50);
        Run(S(false), 60);
        Assert.DoesNotContain(_sink.Commands, c => c.Kind == OutputKind.MouseMove);
    }

    [Fact]
    public void Overlay_NavigationIsMaskedFromBindings()
    {
        Apply(new Profile
        {
            Id = "g",
            Bindings = { new Binding { Input = "M1", Action = ActionDef.KeyPress("F13") } },
        });
        var navs = new List<OverlayNav>();
        _engine.OverlayNavigated += n => navs.Add(n);
        _engine.SetOverlayOpen(true);
        Run(new ControllerState(), 0);
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.M1) }, 10);
        Assert.Equal(new[] { OverlayNav.Up }, navs);
        Assert.DoesNotContain(_sink.Commands, c => c.Kind == OutputKind.KeyDown);
    }

    [Fact]
    public void BindingRequests_AreForwarded()
    {
        Apply(new Profile
        {
            Id = "g",
            Bindings = { new Binding { Input = "B", Action = new ActionDef { Type = ActionType.Profile, Id = "desktop" } } },
        });
        EngineRequest? req = null;
        _engine.Requested += r => req = r;
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.B) }, 0);
        Assert.Equal("desktop", req?.ProfileId);
    }

    [Fact]
    public void Stop_ReleasesHeldOutputs()
    {
        Apply(new Profile { Id = "a", Bindings = { new Binding { Input = "A", Action = ActionDef.KeyPress("W") } } });
        Run(new ControllerState { Buttons = InputSet.Of(LogicalInput.A) }, 0);
        _engine.Stop();
        Assert.Contains(OutputCommand.KeyUp("W"), _sink.Commands);
    }

    [Fact]
    public void Thread_ProcessesStates()
    {
        var sink = new RecordingSink();
        using var engine = new PadEngine(sink);
        engine.Start();
        engine.SetProfile(new Profile { Id = "a", Bindings = { new Binding { Input = "A", Action = ActionDef.KeyPress("W") } } });
        engine.PostState(new ControllerState { Buttons = InputSet.Of(LogicalInput.A) });
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !sink.Commands.Contains(OutputCommand.KeyDown("W"))) Thread.Sleep(10);
        Assert.Contains(OutputCommand.KeyDown("W"), sink.Commands);
        engine.Stop();
        Assert.Contains(OutputCommand.KeyUp("W"), sink.Commands);
    }
}
