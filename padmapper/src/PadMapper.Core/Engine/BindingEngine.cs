using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

public enum EngineRequestKind { SwitchProfile, TogglePause, ToggleGyro, OpenOverlay }

public readonly record struct EngineRequest(EngineRequestKind Kind, string? ProfileId = null);

/// <summary>
/// ボタンのバインディング判定（押下・離す・短押し・長押し・ダブルタップ・同時押し・モードシフト）。
/// 入力集合と時刻を受け取り、OutputTracker に出力命令を積む。時刻はミリ秒（テストでは任意の値を渡せる）。
/// </summary>
public sealed class BindingEngine
{
    private sealed class Runtime
    {
        public required Binding Binding;
        public LogicalInput Input;
        public InputSet ChordSet;
        public List<MacroStep>? Macro;
        public string? Key;
        public List<string> Modifiers = new();
        public bool Active;
        public bool Fired;
        public double LastRise = double.NegativeInfinity;
    }

    private readonly OutputTracker _out;
    private readonly List<Runtime> _rt = new();
    private readonly List<Runtime> _shifts = new();
    private readonly double[] _pressStart = new double[64];
    private readonly List<(double due, int seq, Action op)> _scheduled = new();
    private readonly List<EngineRequest> _requests = new();
    private int _seq;
    private InputSet _prev = InputSet.Empty;

    public Profile Profile { get; private set; } = new();
    public bool Paused { get; set; }
    public string? ActiveLayer { get; private set; }
    public InputSet LastInputs => _prev;

    /// <summary>Update の後に、プロファイル切替などの要求が届く。</summary>
    public event Action<EngineRequest>? Request;

    public BindingEngine(OutputTracker output) => _out = output;

    /// <summary>プロファイルを読み込む。押されているものは呼び出し側で先に ReleaseAll すること。</summary>
    public void Load(Profile profile)
    {
        Profile = profile;
        _rt.Clear();
        _shifts.Clear();
        _scheduled.Clear();
        ActiveLayer = null;
        var bindings = profile.Bindings.ToList();
        if (profile.Overlay.Trigger is { } ot)
            bindings.Add(new Binding
            {
                Input = ot.Input, Trigger = ot.Trigger, HoldMs = ot.HoldMs, DoubleMs = ot.DoubleMs, Chord = ot.Chord,
                Action = new ActionDef { Type = ActionType.Overlay },
            });

        foreach (var b in bindings)
        {
            if (!LogicalInputs.TryParse(b.Input, out var input)) continue;
            // 切り替え時に押しっぱなしのボタンで、新しいプロファイルの長押しがすぐ発火しないようにする
            var rt = new Runtime { Binding = b, Input = input, ChordSet = InputSet.Of(input), Fired = _prev.Has(input) };
            if (b.Trigger == TriggerKind.Chord && b.Chord != null)
                foreach (var c in b.Chord)
                    if (LogicalInputs.TryParse(c, out var ci)) rt.ChordSet = rt.ChordSet.With(ci);
            if (b.Action.Type == ActionType.Key)
            {
                rt.Key = KeyCatalog.Normalize(b.Action.Key);
                foreach (var m in b.Action.Modifiers ?? new List<string>())
                    if (KeyCatalog.Normalize(m) is { } mk) rt.Modifiers.Add(mk);
            }
            if (b.Action.Type == ActionType.Macro) rt.Macro = MacroParser.Parse(b.Action.Macro, out _);

            if (b.Trigger == TriggerKind.ModeShift)
            {
                // モードシフトはデスクトップ型のみ（ゲーム型では元の入力がゲームに届くため無効）
                if (profile.Type == ProfileType.Desktop && !string.IsNullOrEmpty(b.Layer)) _shifts.Add(rt);
                continue;
            }
            _rt.Add(rt);
        }
    }

    /// <summary>押されている出力を全て離し、途中の判定と予約をやめる。押しっぱなしのボタンは「押された」とみなし直さない。</summary>
    public void ReleaseAll()
    {
        _out.ReleaseAll();
        _scheduled.Clear();
        foreach (var rt in _rt)
        {
            rt.Active = false;
            rt.Fired = true; // 押しっぱなしの長押しを、離すまで再発火させない
        }
    }

    /// <summary>入力を全て離した状態に戻す（デバイス切断時など）。</summary>
    public void ResetInputs()
    {
        ReleaseAll();
        _prev = InputSet.Empty;
        ActiveLayer = null;
        foreach (var rt in _rt)
        {
            rt.Fired = false;
            rt.LastRise = double.NegativeInfinity;
        }
    }

    public void Update(InputSet now, double t)
    {
        var rising = now & ~_prev;
        var falling = _prev & ~now;
        foreach (var i in rising.Items()) _pressStart[(int)i] = t;

        RunScheduled(t);
        ActiveLayer = ComputeLayer(now);
        var layerInputs = InputSet.Empty;
        if (ActiveLayer != null)
            foreach (var rt in _rt)
                if (string.Equals(rt.Binding.Layer, ActiveLayer, StringComparison.OrdinalIgnoreCase))
                    layerInputs = layerInputs.With(rt.Input);

        foreach (var rt in _rt)
        {
            var b = rt.Binding;
            bool eligible = ActiveLayer == null
                ? string.IsNullOrEmpty(b.Layer)
                : string.Equals(b.Layer, ActiveLayer, StringComparison.OrdinalIgnoreCase)
                  || (string.IsNullOrEmpty(b.Layer) && !layerInputs.Has(rt.Input));
            bool held = now.Has(rt.Input);
            bool rose = rising.Has(rt.Input);
            bool fell = falling.Has(rt.Input);

            if (!eligible)
            {
                if (rt.Active) End(rt);
                if (fell) rt.Fired = false;
                continue;
            }

            switch (b.Trigger)
            {
                case TriggerKind.Press:
                    if (rose && !rt.Active) Begin(rt, t);
                    else if (!held && rt.Active) End(rt);
                    break;

                case TriggerKind.Release:
                    if (fell) Pulse(rt, t);
                    break;

                case TriggerKind.Tap:
                    if (fell && t - _pressStart[(int)rt.Input] < b.EffectiveHoldMs) Pulse(rt, t);
                    break;

                case TriggerKind.Hold:
                    if (held && !rt.Fired && t - _pressStart[(int)rt.Input] >= b.EffectiveHoldMs)
                    {
                        rt.Fired = true;
                        Begin(rt, t);
                    }
                    if (!held)
                    {
                        if (rt.Active) End(rt);
                        rt.Fired = false;
                    }
                    break;

                case TriggerKind.Double:
                    if (rose)
                    {
                        if (t - rt.LastRise <= b.EffectiveDoubleMs)
                        {
                            rt.LastRise = double.NegativeInfinity;
                            Begin(rt, t);
                        }
                        else rt.LastRise = t;
                    }
                    else if (!held && rt.Active) End(rt);
                    break;

                case TriggerKind.Chord:
                {
                    bool all = (now & rt.ChordSet) == rt.ChordSet;
                    bool anyRose = !(rising & rt.ChordSet).IsEmpty;
                    if (all && anyRose && !rt.Active) Begin(rt, t);
                    else if (!all && rt.Active) End(rt);
                    break;
                }
            }
        }

        _prev = now;
        if (_requests.Count > 0)
        {
            var list = _requests.ToList();
            _requests.Clear();
            foreach (var r in list) Request?.Invoke(r);
        }
    }

    /// <summary>次に判定が必要な時刻（長押しのしきい値やマクロの予約）。なければ null。</summary>
    public double? NextDeadline()
    {
        double? next = null;
        foreach (var s in _scheduled)
            next = next == null ? s.due : Math.Min(next.Value, s.due);
        foreach (var rt in _rt)
            if (rt.Binding.Trigger == TriggerKind.Hold && !rt.Fired && _prev.Has(rt.Input))
            {
                double due = _pressStart[(int)rt.Input] + rt.Binding.EffectiveHoldMs;
                next = next == null ? due : Math.Min(next.Value, due);
            }
        return next;
    }

    private string? ComputeLayer(InputSet now)
    {
        Runtime? latest = null;
        foreach (var s in _shifts)
            if (now.Has(s.Input) && (latest == null || _pressStart[(int)s.Input] > _pressStart[(int)latest.Input]))
                latest = s;
        return latest?.Binding.Layer;
    }

    private bool BlockedByPause(ActionType type) => Paused && type is not (ActionType.Pause or ActionType.Overlay);

    private void Begin(Runtime rt, double t)
    {
        var a = rt.Binding.Action;
        if (BlockedByPause(a.Type)) return;
        switch (a.Type)
        {
            case ActionType.Key:
                if (rt.Key == null) return;
                foreach (var m in rt.Modifiers) _out.KeyDown(m);
                _out.KeyDown(rt.Key);
                rt.Active = true;
                break;
            case ActionType.MouseButton:
                _out.MouseDown(a.Button ?? MouseButton.Left);
                rt.Active = true;
                break;
            case ActionType.Scroll:
                int wheel = (int)Math.Round((a.Amount ?? 0) * 120);
                if (wheel != 0) _out.Add(OutputCommand.ScrollBy(wheel, a.Horizontal == true));
                break;
            case ActionType.MouseMove:
                _out.Add(OutputCommand.Move(a.Dx ?? 0, a.Dy ?? 0));
                break;
            case ActionType.Macro:
                ScheduleMacro(rt.Macro, t);
                break;
            case ActionType.Profile:
                if (!string.IsNullOrWhiteSpace(a.Id)) _requests.Add(new EngineRequest(EngineRequestKind.SwitchProfile, a.Id));
                break;
            case ActionType.Pause:
                _requests.Add(new EngineRequest(EngineRequestKind.TogglePause));
                break;
            case ActionType.GyroToggle:
                _requests.Add(new EngineRequest(EngineRequestKind.ToggleGyro));
                break;
            case ActionType.Overlay:
                _requests.Add(new EngineRequest(EngineRequestKind.OpenOverlay));
                break;
        }
    }

    private void End(Runtime rt)
    {
        if (!rt.Active) return;
        rt.Active = false;
        var a = rt.Binding.Action;
        switch (a.Type)
        {
            case ActionType.Key:
                if (rt.Key == null) return;
                _out.KeyUp(rt.Key);
                for (int i = rt.Modifiers.Count - 1; i >= 0; i--) _out.KeyUp(rt.Modifiers[i]);
                break;
            case ActionType.MouseButton:
                _out.MouseUp(a.Button ?? MouseButton.Left);
                break;
        }
    }

    /// <summary>1回だけ押して離す。キーとマウスボタンは少し待ってから離す（取りこぼし防止）。</summary>
    private void Pulse(Runtime rt, double t)
    {
        var a = rt.Binding.Action;
        if (BlockedByPause(a.Type)) return;
        if (a.Type is ActionType.Key or ActionType.MouseButton)
        {
            Begin(rt, t);
            if (!rt.Active) return;
            rt.Active = false;
            var key = rt.Key;
            var mods = rt.Modifiers.ToList();
            var button = a.Button ?? MouseButton.Left;
            Schedule(t + MacroParser.TapMs, () =>
            {
                if (a.Type == ActionType.Key && key != null)
                {
                    _out.KeyUp(key);
                    for (int i = mods.Count - 1; i >= 0; i--) _out.KeyUp(mods[i]);
                }
                else _out.MouseUp(button);
            });
        }
        else Begin(rt, t);
    }

    private void ScheduleMacro(List<MacroStep>? steps, double t)
    {
        if (steps == null) return;
        double at = t;
        foreach (var s in steps)
        {
            var step = s;
            switch (step.Kind)
            {
                case MacroStepKind.Wait: at += step.Ms; break;
                case MacroStepKind.KeyDown: Schedule(at, () => _out.KeyDown(step.Key!)); break;
                case MacroStepKind.KeyUp: Schedule(at, () => _out.KeyUp(step.Key!)); break;
                case MacroStepKind.MouseDown: Schedule(at, () => _out.MouseDown(step.Button)); break;
                case MacroStepKind.MouseUp: Schedule(at, () => _out.MouseUp(step.Button)); break;
                case MacroStepKind.Scroll:
                    Schedule(at, () => _out.Add(OutputCommand.ScrollBy((int)Math.Round(step.Amount * 120), step.Horizontal)));
                    break;
                case MacroStepKind.Move: Schedule(at, () => _out.Add(OutputCommand.Move(step.Dx, step.Dy))); break;
            }
        }
        RunScheduled(t);
    }

    private void Schedule(double due, Action op) => _scheduled.Add((due, _seq++, op));

    private void RunScheduled(double t)
    {
        if (_scheduled.Count == 0) return;
        var ready = _scheduled.Where(s => s.due <= t).OrderBy(s => s.due).ThenBy(s => s.seq).ToList();
        if (ready.Count == 0) return;
        foreach (var r in ready) _scheduled.Remove(r);
        foreach (var r in ready) r.op();
    }
}
