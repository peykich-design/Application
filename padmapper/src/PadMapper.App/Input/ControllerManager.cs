using System.Diagnostics;
using PadMapper.Core.Devices;
using PadMapper.Core.Engine;
using PadMapper.Core.Logging;
using PadMapper.Core.Model;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Input;

/// <summary>
/// コントローラーの検出・受信・解析をまとめる（IControllerSource の実装）。
/// - 第一候補は Raw Input（非排他・バックグラウンドでも受信）
/// - 標準の HID ゲームパッドは HidP で解析（ボタン番号・軸）、ベンダー独自のレポートはバイト位置で解析
/// - 1台が複数のコレクション（標準 + ベンダー独自）を持つときは、状態を1つに合成してエンジンに渡す
/// - "auto" でベンダー独自のレポートが Raw Input で届かなければ、そのコレクションだけ HID の直接読み取りに切り替える
/// - 抜き差し・Bluetooth 再接続・スリープ復帰のたびに再列挙して登録し直す
/// </summary>
internal sealed class ControllerManager : IControllerSource
{
    private readonly ILog _log;
    private readonly Func<IReadOnlyList<DeviceDefinition>> _definitions;
    private readonly Func<AppSettings> _settings;
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<IntPtr, string> _handleToPath = new();
    private readonly Dictionary<string, DeviceParser> _parsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HidCollection> _hid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ControllerState> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HidReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastParsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] data, double time)> _lastReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diagnostic = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int, int)> _registered = new();
    private MessageWindowThread? _window;
    private Timer? _refreshTimer;
    private Timer? _fallbackTimer;
    private List<HidDeviceInfo> _devices = new();
    private bool _connected;
    private long _reports;

    public event Action<ControllerState>? StateReceived;
    public event Action<ConnectionChange>? ConnectionChanged;
    public event Action<RawReport>? RawReportReceived;
    /// <summary>デバイス一覧が変わった（任意のスレッドから呼ばれる）。</summary>
    public event Action? DevicesChanged;
    public event Action<int>? HotKey;

    public ControllerManager(ILog log, Func<IReadOnlyList<DeviceDefinition>> definitions, Func<AppSettings> settings)
    {
        _log = log;
        _definitions = definitions;
        _settings = settings;
    }

    public MessageWindowThread? Window => _window;

    public IReadOnlyList<HidDeviceInfo> Devices
    {
        get { lock (_lock) return _devices.ToList(); }
    }

    public bool IsConnected => _connected;
    public long ReportCount => Interlocked.Read(ref _reports);
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>使用中のコレクションと、その定義（ホーム・診断画面の表示用）。</summary>
    public IReadOnlyList<(HidDeviceInfo device, DeviceDefinition definition)> ActiveCollections
    {
        get
        {
            lock (_lock)
                return _devices.Where(d => _parsers.ContainsKey(d.Path))
                    .Select(d => (d, _parsers[d.Path].Definition)).ToList();
        }
    }

    /// <summary>主に表示するデバイス（解析できたレポートが届いたもの → 一致したもの の順）。</summary>
    public HidDeviceInfo? ActiveDevice
    {
        get
        {
            lock (_lock)
            {
                var path = _lastParsed.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault()
                           ?? _parsers.Keys.FirstOrDefault();
                return path == null ? null : _devices.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    public DeviceDefinition? ActiveDefinition
    {
        get
        {
            var d = ActiveDevice;
            lock (_lock) return d != null && _parsers.TryGetValue(d.Path, out var p) ? p.Definition : null;
        }
    }

    /// <summary>仮の配置（汎用定義）のまま使っているコレクションがあり、設定が必要か。</summary>
    public bool NeedsSetup
    {
        get { lock (_lock) return _parsers.Values.Any(p => BuiltInDevices.IsGeneric(p.Definition)); }
    }

    public double? LastParsedMs
    {
        get
        {
            lock (_lock) return _lastParsed.Count == 0 ? null : _lastParsed.Values.Max();
        }
    }

    public string ActiveMethod
    {
        get
        {
            lock (_lock) return _readers.Keys.Any(_parsers.ContainsKey) ? "RawInput + HID" : "RawInput";
        }
    }

    public bool InitCommandSent
    {
        get { lock (_lock) return _initSent.Count > 0; }
    }

    /// <summary>そのコレクションの直近の生レポート（診断情報のコピー用）。</summary>
    public (byte[] data, double time)? LastReport(string path)
    {
        lock (_lock) return _lastReports.TryGetValue(path, out var r) ? r : null;
    }

    /// <summary>そのデバイスに使う定義（ウィザードで作った定義 → 組み込みの順。組み込みは先にあるほど優先）。</summary>
    public DeviceDefinition? FindDefinition(HidDeviceInfo d)
    {
        var defs = _definitions();
        return defs.Where(x => x.Source == "learned").FirstOrDefault(x => x.Match.Matches(d))
               ?? defs.FirstOrDefault(x => x.Source != "learned" && x.Match.Matches(d));
    }

    // ---------- 診断 ----------

    /// <summary>生レポートを RawReportReceived で通知してほしいコレクションを足す（Raw Input にも登録する）。</summary>
    public void AddDiagnostic(HidDeviceInfo d)
    {
        lock (_lock) _diagnostic.Add(d.Path);
        EnsureRegistered(d);
    }

    public void RemoveDiagnostic(string path)
    {
        lock (_lock) _diagnostic.Remove(path);
    }

    public void ClearDiagnostics()
    {
        lock (_lock) _diagnostic.Clear();
    }

    /// <summary>標準の HID ゲームパッドのレポートを HidP で解析する（設定画面用）。標準のゲームパッドでなければ null。</summary>
    public HidSnapshot? ParseHid(string path, byte[] data)
    {
        HidCollection? c;
        lock (_lock) _hid.TryGetValue(path, out c);
        return c?.Parse(data);
    }

    public bool IsHidGamepad(string path)
    {
        lock (_lock) return _hid.ContainsKey(path);
    }

    // ---------- 起動・列挙 ----------

    public void Start()
    {
        _window = new MessageWindowThread(_log);
        _window.HidReport += OnRawReport;
        _window.RawDeviceChanged += (arrival, h) =>
        {
            if (!arrival)
                lock (_lock) _handleToPath.Remove(h);
            ScheduleRefresh(300);
        };
        _window.DeviceInterfaceChanged += () => ScheduleRefresh(300);
        _window.HotKey += id => HotKey?.Invoke(id);
        _window.Start();
        _refreshTimer = new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
        _fallbackTimer = new Timer(_ => CheckFallback(), null, Timeout.Infinite, Timeout.Infinite);
        Refresh();
    }

    /// <summary>少し待ってから再列挙する（抜き差し直後はデバイスが揃っていないことがあるため）。</summary>
    public void ScheduleRefresh(int delayMs) => _refreshTimer?.Change(delayMs, Timeout.Infinite);

    /// <summary>入力方式や定義の変更など、全部作り直す。</summary>
    public void Restart()
    {
        lock (_lock)
        {
            foreach (var r in _readers.Values) r.Dispose();
            _readers.Clear();
            _lastParsed.Clear();
            _initSent.Clear();
            _parsers.Clear();
            _parts.Clear();
        }
        ScheduleRefresh(100);
    }

    public void Refresh()
    {
        List<HidDeviceInfo> devices;
        try
        {
            devices = HidEnumerator.Enumerate(_log);
        }
        catch (Exception ex)
        {
            _log.Error("HID の列挙に失敗", ex);
            return;
        }

        var settings = _settings();
        string method = settings.InputMethod;
        var toRegister = new List<(int, int)> { (0x01, 0x04), (0x01, 0x05) };
        bool wasConnected, nowConnected;
        string summary = "";

        lock (_lock)
        {
            _devices = devices;
            var present = devices.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // いなくなったデバイスの後始末
            foreach (var path in _parsers.Keys.Where(p => !present.Contains(p)).ToList()) _parsers.Remove(path);
            foreach (var path in _parts.Keys.Where(p => !present.Contains(p)).ToList()) _parts.Remove(path);
            foreach (var path in _hid.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _hid[path].Dispose();
                _hid.Remove(path);
            }
            foreach (var path in _readers.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _readers[path].Dispose();
                _readers.Remove(path);
            }
            foreach (var path in _lastParsed.Keys.Where(p => !present.Contains(p)).ToList()) _lastParsed.Remove(path);
            foreach (var path in _lastReports.Keys.Where(p => !present.Contains(p)).ToList()) _lastReports.Remove(path);
            _initSent.RemoveWhere(p => !present.Contains(p));

            foreach (var d in devices)
            {
                // 標準のゲームパッドは HidP の解析情報を用意しておく（設定画面でも使う）
                if (d.IsCandidate && !_hid.ContainsKey(d.Path) && HidCollection.Open(d, _log) is { } hc) _hid[d.Path] = hc;

                var def = FindDefinition(d);
                if (def == null || (def.IsHid && !_hid.ContainsKey(d.Path)))
                {
                    _parsers.Remove(d.Path);
                    _parts.Remove(d.Path);
                    continue;
                }
                if (!_parsers.TryGetValue(d.Path, out var existing) || existing.Definition != def)
                {
                    _parsers[d.Path] = new DeviceParser(def);
                    _parts.Remove(d.Path);
                }
                // キーボード・マウスのコレクションは登録しない（全てのマウス移動で起こされてしまうため）
                if (d.UsagePage != 0 && d.IsCandidate) toRegister.Add((d.UsagePage, d.Usage));
            }

            foreach (var path in _diagnostic)
                if (devices.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) is { IsCandidate: true, UsagePage: > 0 } dd)
                    toRegister.Add((dd.UsagePage, dd.Usage));

            wasConnected = _connected;
            nowConnected = _parsers.Count > 0;
            _connected = nowConnected;
            summary = string.Join(", ", devices.Where(d => _parsers.ContainsKey(d.Path))
                .Select(d => $"{d.Product} ({d.VidPid} {d.UsageText}) → {_parsers[d.Path].Definition.Name}"));
        }

        // Raw Input の登録（ウィンドウのスレッドで）。既に登録したものも含めて渡す。
        var list = toRegister.Distinct().ToList();
        _window?.Invoke(() =>
        {
            List<(int, int)> all;
            lock (_lock)
            {
                foreach (var c in list) _registered.Add(c);
                all = _registered.ToList();
            }
            _window.Register(all);
        });

        // HID の直接読み取り
        if (method == "hidRead")
            foreach (var d in devices.Where(d => _parsers.ContainsKey(d.Path))) StartReader(d);
        else if (method == "auto")
            _fallbackTimer?.Change(3000, Timeout.Infinite);

        // 初期化コマンド（設定でオンのときだけ）
        if (settings.SendInitCommand)
            foreach (var d in devices)
            {
                DeviceParser? p;
                lock (_lock) _parsers.TryGetValue(d.Path, out p);
                var hex = p?.Definition.InitCommandHex;
                if (string.IsNullOrEmpty(hex) || d.OutputReportLength == 0) continue;
                bool already;
                lock (_lock) already = !_initSent.Add(d.Path);
                if (!already) HidReader.SendOutputReport(d, hex, _log);
            }

        if (wasConnected != nowConnected) _log.Info(nowConnected ? $"コントローラー接続: {summary}" : "コントローラー切断");
        else if (nowConnected) _log.Info($"コントローラー: {summary}");
        if (wasConnected != nowConnected)
        {
            var active = ActiveDevice;
            ConnectionChanged?.Invoke(new ConnectionChange(nowConnected, active, ActiveDefinition?.Name));
        }
        DevicesChanged?.Invoke();
    }

    /// <summary>ベンダー独自のレポートが Raw Input で届いていないコレクションは、直接読み取りに切り替える。</summary>
    private void CheckFallback()
    {
        if (_settings().InputMethod != "auto") return;
        List<HidDeviceInfo> targets;
        lock (_lock)
            targets = _devices.Where(d => _parsers.TryGetValue(d.Path, out var p) && !p.IsHid
                                          && !_lastParsed.ContainsKey(d.Path) && !_readers.ContainsKey(d.Path)).ToList();
        foreach (var d in targets)
        {
            _log.Info($"Raw Input で受信できないため HID 直接読み取りに切り替え: {d.Product} ({d.UsageText})");
            StartReader(d);
        }
    }

    private void StartReader(HidDeviceInfo d)
    {
        lock (_lock)
            if (_readers.ContainsKey(d.Path)) return;
        var reader = new HidReader(d, _log);
        reader.Report += (path, data) => Dispatch(path, data, fromReader: true);
        reader.Stopped += path =>
        {
            lock (_lock)
                if (_readers.TryGetValue(path, out var r) && r == reader) _readers.Remove(path);
            ScheduleRefresh(1000);
        };
        if (reader.Start())
            lock (_lock) _readers[d.Path] = reader;
        else
            reader.Dispose();
    }

    private void OnRawReport(IntPtr hDevice, byte[] data)
    {
        string? path;
        lock (_lock) _handleToPath.TryGetValue(hDevice, out path);
        if (path == null)
        {
            path = MessageWindowThread.GetDeviceName(hDevice);
            if (path == null) return;
            lock (_lock) _handleToPath[hDevice] = path;
        }
        Dispatch(path, data, fromReader: false);
    }

    private void Dispatch(string path, byte[] data, bool fromReader)
    {
        double now = NowMs;
        bool diag;
        DeviceParser? parser;
        HidCollection? hid;
        lock (_lock)
        {
            _lastReports[path] = (data, now);
            diag = _diagnostic.Contains(path);
            // 直接読み取り中のデバイスは Raw Input 側を無視（二重処理しない）
            if (!fromReader && _readers.ContainsKey(path)) return;
            _parsers.TryGetValue(path, out parser);
            _hid.TryGetValue(path, out hid);
        }
        if (diag) RawReportReceived?.Invoke(new RawReport(path, data, now));
        if (parser == null) return;

        var state = new ControllerState { TimestampMs = now };
        bool ok;
        if (parser.IsHid)
        {
            var snap = hid?.Parse(data);
            ok = snap != null && parser.TryParseHid(snap, state);
        }
        else ok = parser.TryParse(data, state);
        if (!ok) return;

        ControllerState merged;
        lock (_lock)
        {
            _lastParsed[path] = now;
            _parts[path] = state;
            var parts = _parts.Where(kv => _parsers.ContainsKey(kv.Key))
                .Select(kv => (_parsers[kv.Key], kv.Value)).ToList();
            merged = parts.Count == 1 ? state : StateMerger.Merge(parts, now);
        }
        Interlocked.Increment(ref _reports);
        StateReceived?.Invoke(merged);
    }

    /// <summary>診断用に、そのデバイスを Raw Input に登録する（ゲームパッド・ベンダー独自のコレクションのみ）。</summary>
    public void EnsureRegistered(HidDeviceInfo d)
    {
        if (!d.IsCandidate || d.UsagePage == 0) return;
        _window?.Invoke(() =>
        {
            List<(int, int)> list;
            lock (_lock)
            {
                _registered.Add((d.UsagePage, d.Usage));
                list = _registered.ToList();
            }
            _window.Register(list);
        });
    }

    /// <summary>診断用：Raw Input で届かないデバイスを直接読む。</summary>
    public HidReader? OpenDiagnosticReader(HidDeviceInfo d)
    {
        var reader = new HidReader(d, _log);
        reader.Report += (path, data) => RawReportReceived?.Invoke(new RawReport(path, data, NowMs));
        if (reader.Start()) return reader;
        reader.Dispose();
        return null;
    }

    public void Stop()
    {
        _refreshTimer?.Dispose();
        _fallbackTimer?.Dispose();
        lock (_lock)
        {
            foreach (var r in _readers.Values) r.Dispose();
            _readers.Clear();
            foreach (var h in _hid.Values) h.Dispose();
            _hid.Clear();
        }
        _window?.Dispose();
        _window = null;
    }

    public void Dispose() => Stop();
}
