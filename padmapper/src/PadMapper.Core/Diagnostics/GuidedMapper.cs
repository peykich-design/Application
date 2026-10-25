using PadMapper.Core.Devices;

namespace PadMapper.Core.Diagnostics;

public enum SetupStepKind { Rest, StickRight, StickDown, Trigger, Button }

/// <summary>設定の1手順。Target は LX/LY/RX/RY/LT/RT かボタン名。</summary>
public sealed record SetupStep(string Target, SetupStepKind Kind);

/// <summary>1つの手順で決まった割り当て。軸のときは Rest（静止時）と Peak（倒しきったとき）の値（0〜1）も持つ。</summary>
public sealed record SetupAssignment(SetupStep Step, string Source, string Signal, string Description, double Rest = 0, double Peak = 0)
{
    public bool IsAxis => Signal.StartsWith("axis:") || Signal.StartsWith("u8:");
}

/// <summary>
/// 案内つきのコントローラー設定（「A ボタンを押してください」→ 押されたら自動で次へ）。
/// 入力は複数のコレクション（標準の HID ゲームパッドとベンダー独自のレポート）から同時に受け取る。
/// <list type="bullet">
/// <item>最初に「何も触らない」状態を記録し、静止時の値を覚える</item>
/// <item>スティック・トリガーを先に設定する。生レポートでは、ここで値が何通りにも変わったバイトを「アナログ」とみなし、
///   以降のボタン判定の候補から外す（スティックの揺れをボタンと誤検知しないため）</item>
/// <item>ボタンは「静止時は離れていて、押して離した」信号だけを採用する。HID のボタンを生レポートのビットより優先する</item>
/// </list>
/// </summary>
public sealed class GuidedMapper
{
    public const double RestMs = 1000;
    private const double AxisThreshold = 0.45;
    private const double AxisReturn = 0.2;

    private sealed class SourceState
    {
        public bool IsHid;
        public Dictionary<string, bool> Buttons = new();
        public Dictionary<string, double> Axes = new();   // 0..1 に正規化
        public Dictionary<string, double> RestAxes = new();
        public HashSet<string> RestOn = new();
        public Dictionary<int, HashSet<byte>> ByteValues = new();
        public byte[]? RestReport;
        public bool HasRest;
    }

    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SetupAssignment> _done = new();
    private readonly HashSet<string> _lateSources = new(StringComparer.OrdinalIgnoreCase);
    private int _index;
    private double _stepStart = double.NaN;

    // 手順の途中の状態
    private (string source, string signal)? _candidate;
    private bool _candidateIsAxis;
    private readonly List<(string source, string signal)> _pressed = new();
    private double _candidateSince;
    private double _peak;
    private double _peakValue;

    public IReadOnlyList<SetupStep> Steps { get; }
    public IReadOnlyList<SetupAssignment> Assignments => _done;
    public int Index => _index;
    public SetupStep? Current => _index < Steps.Count ? Steps[_index] : null;
    public bool IsFinished => _index >= Steps.Count;
    /// <summary>今の手順で、候補を検出して「離すのを待っている」状態か。</summary>
    public bool IsWaitingRelease => _candidate != null;
    /// <summary>検出中の入力の説明（例 "Button 5"）。</summary>
    public string? CandidateDescription => _candidate is { } c ? Describe(c.signal) : null;

    public static IReadOnlyList<SetupStep> DefaultSteps(bool includeBackButtons = true)
    {
        var list = new List<SetupStep>
        {
            new("rest", SetupStepKind.Rest),
            new("LX", SetupStepKind.StickRight), new("LY", SetupStepKind.StickDown),
            new("RX", SetupStepKind.StickRight), new("RY", SetupStepKind.StickDown),
            new("LT", SetupStepKind.Trigger), new("RT", SetupStepKind.Trigger),
        };
        foreach (var b in new[] { "A", "B", "X", "Y", "LB", "RB", "Back", "Start", "Guide", "L3", "R3",
                     "DPadUp", "DPadDown", "DPadLeft", "DPadRight" })
            list.Add(new SetupStep(b, SetupStepKind.Button));
        if (includeBackButtons)
            foreach (var b in new[] { "M1", "M2", "M3", "M4", "C", "Z" })
                list.Add(new SetupStep(b, SetupStepKind.Button));
        return list;
    }

    public GuidedMapper(IReadOnlyList<SetupStep>? steps = null) => Steps = steps ?? DefaultSteps();

    // ---------- 入力 ----------

    /// <summary>標準の HID ゲームパッドの値（HidP で解析済み）。</summary>
    public void FeedHid(string source, HidSnapshot snap, double nowMs)
    {
        var s = Source(source, isHid: true);
        s.Buttons.Clear();
        foreach (var b in snap.Buttons) s.Buttons["btn:" + b] = true;
        foreach (var dir in new[] { "up", "down", "left", "right" })
            s.Buttons["hat:" + dir] = HidSnapshot.HatHas(snap.Hat, dir);
        s.Axes.Clear();
        foreach (var (usage, v) in snap.Values)
            if (usage != HidSnapshot.UsageHat) s.Axes["axis:" + usage] = v / (double)HidRange.Full;
        Evaluate(nowMs);
    }

    /// <summary>ベンダー独自などの生レポート。ビットをボタン、バイトを軸の候補にする。</summary>
    public void FeedRaw(string source, ReadOnlySpan<byte> report, double nowMs)
    {
        var s = Source(source, isHid: false);
        s.Buttons.Clear();
        s.Axes.Clear();
        for (int i = 0; i < report.Length; i++)
        {
            if (!s.ByteValues.TryGetValue(i, out var set)) s.ByteValues[i] = set = new HashSet<byte>();
            if (set.Count <= 8) set.Add(report[i]);
            s.Axes["u8:" + i] = report[i] / 255.0;
            if (IsAnalogByte(s, i)) continue;
            for (int b = 0; b < 8; b++) s.Buttons[$"bit:{i}.{b}"] = (report[i] & (1 << b)) != 0;
        }
        if (_index < Steps.Count && Steps[_index].Kind == SetupStepKind.Rest) s.RestReport = report.ToArray();
        Evaluate(nowMs);
    }

    /// <summary>何通りもの値をとったバイトはアナログ（スティック・トリガー・ジャイロ）とみなす。</summary>
    private static bool IsAnalogByte(SourceState s, int i) => s.ByteValues.TryGetValue(i, out var set) && set.Count > 3;

    /// <summary>時間経過の判定（入力が来なくても UI のタイマーから呼ぶ）。</summary>
    public void Tick(double nowMs) => Evaluate(nowMs);

    private SourceState Source(string source, bool isHid)
    {
        if (!_sources.TryGetValue(source, out var s))
        {
            _sources[source] = s = new SourceState { IsHid = isHid };
            // 静止の記録より後に届き始めたコレクションは、最初のレポートを静止時の値とみなす
            _lateSources.Add(source);
        }
        return s;
    }

    // ---------- 手順の操作 ----------

    public void Skip()
    {
        if (IsFinished) return;
        if (Steps[_index].Kind == SetupStepKind.Rest) CaptureRest();
        Advance();
    }

    public void Back()
    {
        if (_index == 0) return;
        _index--;
        if (Steps[_index].Kind == SetupStepKind.Rest)
        {
            foreach (var s in _sources.Values) s.HasRest = false;
        }
        _done.RemoveAll(a => a.Step == Steps[_index]);
        ResetStep();
    }

    public void Restart()
    {
        _index = 0;
        _done.Clear();
        _sources.Clear();
        ResetStep();
    }

    private void Advance()
    {
        _index++;
        ResetStep();
    }

    private void ResetStep()
    {
        _stepStart = double.NaN;
        _candidate = null;
        _pressed.Clear();
        _peak = 0;
    }

    // ---------- 判定 ----------

    private void Evaluate(double now)
    {
        foreach (var name in _lateSources)
        {
            var ls = _sources[name];
            if (!ls.HasRest && _index > 0 && !_done.Any(a => a.Source == name))
            {
                ls.RestAxes = new Dictionary<string, double>(ls.Axes);
                ls.RestOn = ls.Buttons.Where(b => b.Value).Select(b => b.Key).ToHashSet();
                ls.HasRest = true;
            }
        }
        _lateSources.Clear();
        if (IsFinished) return;
        if (double.IsNaN(_stepStart)) _stepStart = now;
        var step = Steps[_index];
        switch (step.Kind)
        {
            case SetupStepKind.Rest:
                if (_sources.Count > 0 && now - _stepStart >= RestMs)
                {
                    CaptureRest();
                    Advance();
                }
                break;
            case SetupStepKind.Button:
                EvaluateButton(step, now);
                break;
            case SetupStepKind.StickRight:
            case SetupStepKind.StickDown:
                EvaluateAxis(step, stick: true);
                break;
            case SetupStepKind.Trigger:
                // アナログの軸を優先し、無ければデジタルのボタンとして探す
                // 引き始めにデジタルのボタンが先に反応しても、軸が大きく動けば軸を採用する
                if (!EvaluateAxis(step, stick: false)) EvaluateButton(step, now);
                break;
        }
    }

    private void CaptureRest()
    {
        foreach (var s in _sources.Values)
        {
            s.RestAxes = new Dictionary<string, double>(s.Axes);
            s.RestOn = s.Buttons.Where(b => b.Value).Select(b => b.Key).ToHashSet();
            s.HasRest = true;
        }
    }

    private bool Assigned(string source, string signal) =>
        _done.Any(a => a.Source.Equals(source, StringComparison.OrdinalIgnoreCase) && a.Signal == signal);

    private void EvaluateButton(SetupStep step, double now)
    {
        // 押している間に反応した信号を全部集め、全て離されたら HID のボタン → 生レポートのビットの順に1つ選ぶ
        // （同じボタンが2つのレポートに少しずれて届くことがあるため、最初に届いた方では決めない）
        foreach (var (name, s) in _sources)
        {
            if (!s.HasRest) continue;
            foreach (var (signal, on) in s.Buttons)
            {
                if (!on || s.RestOn.Contains(signal) || Assigned(name, signal)) continue;
                if (!s.IsHid && signal.StartsWith("bit:") && IsAnalogByte(s, ByteOf(signal))) continue;
                if (_pressed.Count == 0)
                {
                    _candidateSince = now;
                    _candidateIsAxis = false;
                }
                if (!_pressed.Any(p => p.source == name && p.signal == signal)) _pressed.Add((name, signal));
            }
        }
        if (_pressed.Count == 0) return;
        _candidate = _pressed[0];
        bool anyHeld = _pressed.Any(p => _sources[p.source].Buttons.GetValueOrDefault(p.signal));
        if (anyHeld) return;

        var pick = _pressed.FirstOrDefault(p => _sources[p.source].IsHid);
        if (pick == default) pick = _pressed[0];
        bool longEnough = now - _candidateSince >= 20;
        _pressed.Clear();
        _candidate = null;
        if (!longEnough) return;
        _done.Add(new SetupAssignment(step, pick.source, pick.signal, Describe(pick.signal)));
        Advance();
    }

    private static int ByteOf(string bitSignal) => int.Parse(bitSignal[4..bitSignal.IndexOf('.')]);

    /// <summary>軸の手順。倒した（引いた）軸を見つけ、戻したら確定する。軸が見つかれば true。</summary>
    private bool EvaluateAxis(SetupStep step, bool stick)
    {
        if (_candidate is { } c && _candidateIsAxis)
        {
            if (!_sources.TryGetValue(c.source, out var cs) || !cs.Axes.TryGetValue(c.signal, out var v)) return true;
            double rest = cs.RestAxes.GetValueOrDefault(c.signal, v);
            double d = v - rest;
            if (Math.Abs(d) > Math.Abs(_peak))
            {
                _peak = d;
                _peakValue = v;
            }
            if (Math.Abs(d) < AxisReturn)
            {
                _done.Add(new SetupAssignment(step, c.source, c.signal,
                    $"{Describe(c.signal)}{(_peak < 0 ? " (-)" : " (+)")}", rest, _peakValue));
                Advance();
            }
            return true;
        }
        // スティックは既に使った軸を候補から外す。トリガーは、1本の軸を左右で共有する機種があるのでトリガー同士の重複は許す。
        string? bestSource = null, bestSignal = null;
        double best = 0;
        foreach (var hidFirst in new[] { true, false })
        {
            foreach (var (name, s) in _sources)
            {
                if (s.IsHid != hidFirst || !s.HasRest) continue;
                foreach (var (signal, v) in s.Axes)
                {
                    var used = _done.Where(a => a.Source.Equals(name, StringComparison.OrdinalIgnoreCase) && a.Signal == signal).ToList();
                    if (used.Any(a => stick || a.Step.Kind != SetupStepKind.Trigger)) continue;
                    double d = Math.Abs(v - s.RestAxes.GetValueOrDefault(signal, v));
                    if (d >= AxisThreshold && d > best)
                    {
                        best = d;
                        bestSource = name;
                        bestSignal = signal;
                    }
                }
            }
            if (bestSignal != null) break; // HID の軸が見つかれば生レポートは見ない
        }
        if (bestSignal == null) return false;
        _candidate = (bestSource!, bestSignal);
        _candidateIsAxis = true;
        _pressed.Clear();
        _peak = 0;
        return true;
    }

    private static string Describe(string signal)
    {
        if (signal.StartsWith("btn:")) return "Button " + signal[4..];
        if (signal.StartsWith("hat:")) return "Hat " + signal[4..];
        if (signal.StartsWith("axis:")) return "Axis " + HidSnapshot.UsageName(int.Parse(signal[5..]));
        if (signal.StartsWith("bit:")) return $"[{signal[4..signal.IndexOf('.')]}] bit {signal[(signal.IndexOf('.') + 1)..]}";
        if (signal.StartsWith("u8:")) return $"[{signal[3..]}] u8";
        return signal;
    }

    // ---------- 結果 ----------

    /// <summary>このコレクションの静止時の生レポート（生レポートの定義を作るとき、レポート ID と長さに使う）。</summary>
    public byte[]? RestReportOf(string source) => _sources.TryGetValue(source, out var s) ? s.RestReport : null;

    public IEnumerable<string> SourcesWithAssignments() =>
        _done.Select(a => a.Source).Distinct(StringComparer.OrdinalIgnoreCase);

    public bool IsHidSource(string source) => _sources.TryGetValue(source, out var s) && s.IsHid;

    /// <summary>
    /// 1つのコレクションの定義を作る。baseDefinition（組み込みや保存済み）があれば、それを元に今回の割り当てで上書きする
    /// （ジャイロなど今回設定しない項目を残すため）。HID の定義は今回の割り当てだけで作る。
    /// </summary>
    public DeviceDefinition BuildDefinition(string source, HidDeviceInfo device, DeviceDefinition? baseDefinition)
    {
        bool hid = IsHidSource(source);
        var def = !hid && baseDefinition != null && !baseDefinition.IsHid
            ? ProfilesClone(baseDefinition)
            : new DeviceDefinition();
        def.Kind = hid ? "hid" : "raw";
        def.Id = $"learned-{device.VendorId:x4}{device.ProductId:x4}-{device.UsagePage:x4}{device.Usage:x2}";
        def.Name = (string.IsNullOrEmpty(device.Product) ? device.VidPid : device.Product) + (hid ? "" : " (vendor)");
        def.Source = "learned";
        def.Verified = false;
        def.Match = new DeviceMatch { VendorId = device.VendorId, ProductId = device.ProductId, UsagePage = device.UsagePage, Usage = device.Usage };
        if (!hid)
        {
            var rest = RestReportOf(source);
            if (rest is { Length: > 0 })
            {
                if (def.ReportId == null && rest[0] != 0) def.ReportId = rest[0];
                def.MinLength = Math.Max(1, Math.Min(def.MinLength <= 1 ? rest.Length : def.MinLength, rest.Length));
            }
        }

        foreach (var a in _done.Where(a => a.Source.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            var target = a.Step.Target;
            if (a.IsAxis)
            {
                double rest = a.Rest, peak = a.Peak;
                double full = hid ? HidRange.Full : 255;
                var map = new AxisMap { Axis = target };
                if (hid) map.HidUsage = int.Parse(a.Signal[5..]);
                else
                {
                    map.Offset = int.Parse(a.Signal[3..]);
                    map.Format = "u8";
                }
                if (a.Step.Kind == SetupStepKind.Trigger)
                {
                    map.Min = Math.Round(rest * full);
                    map.Max = Math.Round(peak * full);
                }
                else
                {
                    map.Center = Math.Round(rest * full);
                    map.Min = 0;
                    map.Max = full;
                    // 右・下に倒して値が小さくなる軸は反転
                    map.Invert = peak < rest;
                }
                def.Axes.RemoveAll(x => x.Axis.Equals(target, StringComparison.OrdinalIgnoreCase));
                def.Axes.Add(map);
                continue;
            }

            var b = new ButtonMap { Input = target };
            if (a.Signal.StartsWith("btn:")) b.HidButton = int.Parse(a.Signal[4..]);
            else if (a.Signal.StartsWith("hat:")) b.HidHat = a.Signal[4..];
            else
            {
                int off = ByteOf(a.Signal);
                int bit = int.Parse(a.Signal[(a.Signal.IndexOf('.') + 1)..]);
                b.Offset = off;
                b.Mask = 1 << bit;
                // 元の定義で同じビットを別のボタンに使っていたら外す
                def.Buttons.RemoveAll(x => x.HidButton == null && x.HidHat == null && x.Offset == off && x.Mask == b.Mask && x.Value == null);
            }
            if (a.Step.Kind == SetupStepKind.Trigger) def.Axes.RemoveAll(x => x.Axis.Equals(target, StringComparison.OrdinalIgnoreCase));
            def.Buttons.RemoveAll(x => x.Input.Equals(target, StringComparison.OrdinalIgnoreCase));
            def.Buttons.Add(b);
        }
        return def;
    }

    private static DeviceDefinition ProfilesClone(DeviceDefinition d) =>
        Profiles.ProfileJson.Deserialize<DeviceDefinition>(Profiles.ProfileJson.Serialize(d));
}
