using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using PadMapper.Core.Logging;
using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.Core.Engine;

public enum OverlayNav { Up, Down, Select, Close }

/// <summary>UI に渡す、ある時点のエンジンの状態（読み取り専用）。</summary>
public sealed class EngineSnapshot
{
    public ControllerState? State { get; init; }
    public InputSet Inputs { get; init; }
    public Vector3 GyroRaw { get; init; }
    public Vector3 GyroCorrected { get; init; }
    public Vector3 GyroBias { get; init; }
    public Vector2 GyroOutputDps { get; init; }
    public bool GyroActive { get; init; }
    public bool GyroCalibrated { get; init; }
    public bool GyroGlobalEnabled { get; init; }
    public string ProfileId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public ProfileType ProfileType { get; init; }
    public bool Paused { get; init; }
    public string? Layer { get; init; }
    public bool Connected { get; init; }
    public double LastStateMs { get; init; }
    public int HeldOutputs { get; init; }
    public long StatesProcessed { get; init; }
}

/// <summary>
/// エンジン本体。専用スレッド1本で、入力・時間経過・設定変更を順番に処理する（UI スレッドをブロックしない）。
/// 入力が来ない間は待機するだけなので、アイドル時の CPU 使用はほぼ 0。
/// </summary>
public sealed class PadEngine : IDisposable
{
    private readonly IOutputSink _sink;
    private readonly ILog _log;
    private readonly Func<double> _clock;
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly OutputTracker _tracker = new();
    private readonly BindingEngine _bindings;
    private readonly GyroProcessor _gyro = new();
    private readonly MouseAccumulator _mouse = new();
    private Thread? _thread;
    private volatile bool _running;

    private Profile _profile = new();
    private ControllerState? _state;
    private InputSet _derived = InputSet.Empty;
    private InputSet _masked = InputSet.Empty;
    private double _lastStateMs = double.NaN;
    private double _lastStickMs = double.NaN;
    private bool _stickMoving;
    private bool _paused;
    private bool _gyroGlobal = true;
    private bool _gyroToggle;
    private bool _overlayOpen;
    private bool _connected;
    private long _count;
    private volatile EngineSnapshot _snapshot = new();

    /// <summary>連続出力（スティック→マウス）の更新間隔。</summary>
    public const double StickTickMs = 4;

    /// <summary>プロファイル切替・オーバーレイ表示など、アプリ側で処理する要求（エンジンのスレッドから呼ばれる）。</summary>
    public event Action<EngineRequest>? Requested;
    public event Action<bool>? PausedChanged;
    public event Action<bool>? GyroEnabledChanged;
    public event Action<OverlayNav>? OverlayNavigated;

    public EngineSnapshot Snapshot => _snapshot;
    public bool IsPaused => _paused;
    public bool IsGyroEnabled => _gyroGlobal;
    public Profile Profile => _profile;

    public PadEngine(IOutputSink sink, ILog? log = null, Func<double>? clock = null)
    {
        _sink = sink;
        _log = log ?? NullLog.Instance;
        if (clock == null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed.TotalMilliseconds;
        }
        _clock = clock;
        _bindings = new BindingEngine(_tracker);
        _bindings.Request += OnBindingRequest;
    }

    public double Now => _clock();

    // ---------- スレッド ----------

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "PadMapper.Engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>押されている出力を全て離してから止める。</summary>
    public void Stop()
    {
        if (!_running)
        {
            ReleaseAllInternal();
            return;
        }
        Post(ReleaseAllInternal);
        _running = false;
        _signal.Set();
        _thread?.Join(1000);
        _thread = null;
    }

    private void Loop()
    {
        while (_running)
        {
            int timeout = Timeout.Infinite;
            var next = NextWake();
            if (next is double due) timeout = (int)Math.Clamp(Math.Ceiling(due - Now), 0, 1000);
            _signal.WaitOne(timeout);
            try
            {
                Pump();
            }
            catch (Exception ex)
            {
                _log.Error("エンジンの処理で例外", ex);
                try { ReleaseAllInternal(); } catch { /* 続行 */ }
            }
        }
        // 終了前に残りを処理（ReleaseAll など）
        while (_queue.TryDequeue(out var a)) a();
    }

    /// <summary>溜まった処理を実行し、時間経過の判定を行う。テストではスレッドを使わずに直接呼ぶ。</summary>
    public void Pump()
    {
        while (_queue.TryDequeue(out var action)) action();
        Tick(Now);
    }

    private double? NextWake()
    {
        double? next = _bindings.NextDeadline();
        if (_stickMoving)
        {
            double due = Now + StickTickMs;
            next = next == null ? due : Math.Min(next.Value, due);
        }
        return next;
    }

    private void Post(Action a)
    {
        _queue.Enqueue(a);
        _signal.Set();
    }

    // ---------- 外部からの操作（どのスレッドからでも呼べる） ----------

    public void PostState(ControllerState s) => Post(() => HandleState(s));

    public void SetProfile(Profile p) => Post(() => ApplyProfile(p));

    public void SetPaused(bool paused) => Post(() => ApplyPaused(paused));

    public void TogglePause() => Post(() => ApplyPaused(!_paused));

    public void SetGyroEnabled(bool enabled) => Post(() => ApplyGyroGlobal(enabled));

    public void ToggleGyro() => Post(() => ApplyGyroGlobal(!_gyroGlobal));

    public void SetOverlayOpen(bool open) => Post(() => _overlayOpen = open);

    public void CalibrateGyro() => Post(() => _gyro.StartManualCalibration(Now));

    public void ResetGyroBias() => Post(() => _gyro.ResetBias());

    public void DeviceDisconnected() => Post(() =>
    {
        _bindings.ResetInputs();
        _state = null;
        _derived = _masked = InputSet.Empty;
        _connected = false;
        _stickMoving = false;
        _lastStateMs = double.NaN;
        _mouse.Reset();
        Flush();
        UpdateSnapshot();
    });

    public void ReleaseAll() => Post(ReleaseAllInternal);

    // ---------- エンジンスレッド内の処理 ----------

    private void ApplyProfile(Profile p)
    {
        _bindings.ReleaseAll();
        Flush();
        _profile = p;
        _bindings.Load(p);
        _bindings.Paused = _paused;
        _gyro.Settings = p.Gyro;
        _gyroToggle = false;
        _mouse.Reset();
        _log.Info($"プロファイル適用: {p.Id}");
        UpdateSnapshot();
    }

    private void ApplyPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        _bindings.Paused = paused;
        if (paused)
        {
            _bindings.ReleaseAll();
            _mouse.Reset();
            _stickMoving = false;
            Flush();
        }
        _log.Info(paused ? "一時停止" : "再開");
        PausedChanged?.Invoke(paused);
        UpdateSnapshot();
    }

    private void ApplyGyroGlobal(bool enabled)
    {
        if (_gyroGlobal == enabled) return;
        _gyroGlobal = enabled;
        GyroEnabledChanged?.Invoke(enabled);
        UpdateSnapshot();
    }

    private void ReleaseAllInternal()
    {
        _bindings.ReleaseAll();
        Flush();
    }

    private void OnBindingRequest(EngineRequest r)
    {
        switch (r.Kind)
        {
            case EngineRequestKind.TogglePause: ApplyPaused(!_paused); break;
            case EngineRequestKind.ToggleGyro: ApplyGyroGlobal(!_gyroGlobal); break;
            default: Requested?.Invoke(r); break;
        }
    }

    private void HandleState(ControllerState s)
    {
        double t = Now;
        double dt = double.IsNaN(_lastStateMs) ? 0 : Math.Clamp((t - _lastStateMs) / 1000.0, 0, 0.05);
        _lastStateMs = t;
        _state = s;
        _connected = true;
        _count++;

        var prevDerived = _derived;
        _derived = StickProcessor.Derive(s, _profile, prevDerived);
        var rising = _derived & ~prevDerived;

        // オーバーレイ表示中は、メニュー操作のボタンをバインディングに渡さない
        var mask = InputSet.Empty;
        if (_overlayOpen)
        {
            var o = _profile.Overlay;
            void Nav(string name, OverlayNav nav)
            {
                if (!LogicalInputs.TryParse(name, out var i)) return;
                mask = mask.With(i);
                if (rising.Has(i)) OverlayNavigated?.Invoke(nav);
            }
            Nav(o.Up, OverlayNav.Up);
            Nav(o.Down, OverlayNav.Down);
            Nav(o.Select, OverlayNav.Select);
            Nav(o.Close, OverlayNav.Close);
        }
        _masked = _derived & ~mask;

        // ジャイロのトグル
        var act = _profile.Gyro.Activation;
        if (act.Mode == GyroActivationMode.Toggle && LogicalInputs.TryParse(act.Input, out var ti) && (rising & ~mask).Has(ti))
            _gyroToggle = !_gyroToggle;

        _bindings.Update(_masked, t);

        if (s.HasGyro)
        {
            var move = _gyro.Process(s.Gyro, s.HasAccel ? s.Accel : null, t, dt, IsGyroActive());
            if (move != Vector2.Zero)
            {
                var (dx, dy) = _mouse.AddMove(move.X, move.Y);
                if (dx != 0 || dy != 0) _tracker.Add(OutputCommand.Move(dx, dy));
            }
        }

        StickStep(t);
        Flush();
        UpdateSnapshot();
    }

    private bool IsGyroActive()
    {
        var g = _profile.Gyro;
        if (!g.Enabled || !_gyroGlobal || _paused) return false;
        return g.Activation.Mode switch
        {
            GyroActivationMode.Always => true,
            GyroActivationMode.WhileHeld => LogicalInputs.TryParse(g.Activation.Input, out var i) && _masked.Has(i),
            GyroActivationMode.Toggle => _gyroToggle,
            _ => false,
        };
    }

    private void Tick(double t)
    {
        _bindings.Update(_masked, t);
        StickStep(t);
        if (_tracker.HasPending) Flush();
    }

    /// <summary>スティック → マウス移動・スクロール（デスクトップ型のみ）。前回からの経過時間で移動量を決める。</summary>
    private void StickStep(double t)
    {
        double dt = double.IsNaN(_lastStickMs) ? 0 : Math.Clamp((t - _lastStickMs) / 1000.0, 0, 0.05);
        _lastStickMs = t;
        _stickMoving = false;
        if (_state == null || _paused || _profile.Type != ProfileType.Desktop) return;

        StickOne(_state.LX, _state.LY, _profile.Sticks.Left, dt);
        StickOne(_state.RX, _state.RY, _profile.Sticks.Right, dt);
    }

    private void StickOne(float x, float y, StickSettings s, double dt)
    {
        if (s.Mode == StickMode.None) return;
        var v = StickProcessor.Process(x, y, s);
        if (v == Vector2.Zero) return;
        _stickMoving = true;
        if (dt <= 0) return;
        if (s.Mode == StickMode.Mouse)
        {
            var (dx, dy) = _mouse.AddMove(v.X * s.Speed * dt, v.Y * s.Speed * dt);
            if (dx != 0 || dy != 0) _tracker.Add(OutputCommand.Move(dx, dy));
        }
        else
        {
            int wv = _mouse.AddScroll(-v.Y * s.Speed * dt, horizontal: false);
            if (wv != 0) _tracker.Add(OutputCommand.ScrollBy(wv, false));
            int wh = _mouse.AddScroll(v.X * s.Speed * dt, horizontal: true);
            if (wh != 0) _tracker.Add(OutputCommand.ScrollBy(wh, true));
        }
    }

    private void Flush()
    {
        if (!_tracker.HasPending) return;
        var cmds = _tracker.Drain();
        try
        {
            _sink.Send(cmds);
        }
        catch (Exception ex)
        {
            _log.Error("出力に失敗", ex);
        }
    }

    private void UpdateSnapshot()
    {
        _snapshot = new EngineSnapshot
        {
            State = _state,
            Inputs = _derived,
            GyroRaw = _gyro.LastRaw,
            GyroCorrected = _gyro.LastCorrected,
            GyroBias = _gyro.Bias,
            GyroOutputDps = _gyro.LastOutputDps,
            GyroActive = _gyro.LastActive,
            GyroCalibrated = _gyro.IsCalibrated,
            GyroGlobalEnabled = _gyroGlobal,
            ProfileId = _profile.Id,
            ProfileName = _profile.Name,
            ProfileType = _profile.Type,
            Paused = _paused,
            Layer = _bindings.ActiveLayer,
            Connected = _connected,
            LastStateMs = _lastStateMs,
            HeldOutputs = _tracker.HeldCount,
            StatesProcessed = _count,
        };
    }

    public void Dispose()
    {
        Stop();
        _signal.Dispose();
    }
}
