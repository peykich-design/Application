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
/// - "auto" で Raw Input から解析できるレポートが届かなければ、HID の直接読み取りに切り替える
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
    private readonly Dictionary<string, HidReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _lastParsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int, int)> _registered = new();
    private MessageWindowThread? _window;
    private Timer? _refreshTimer;
    private Timer? _fallbackTimer;
    private List<HidDeviceInfo> _devices = new();
    private bool _connected;
    private string? _activePath;
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

    /// <summary>診断画面で生レポートを見ているデバイス（null なら通知しない）。</summary>
    public string? DiagnosticPath { get; set; }

    public bool IsConnected => _connected;
    public long ReportCount => Interlocked.Read(ref _reports);
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public HidDeviceInfo? ActiveDevice
    {
        get
        {
            lock (_lock) return _activePath == null ? null : _devices.FirstOrDefault(d => d.Path.Equals(_activePath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DeviceDefinition? ActiveDefinition
    {
        get
        {
            lock (_lock) return _activePath != null && _parsers.TryGetValue(_activePath, out var p) ? p.Definition : null;
        }
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
            lock (_lock)
                return _activePath != null && _readers.ContainsKey(_activePath) ? "HID" : "RawInput";
        }
    }

    public bool InitCommandSent
    {
        get { lock (_lock) return _initSent.Count > 0; }
    }

    /// <summary>そのデバイスに使う定義（ウィザードで作った定義を優先）。</summary>
    public DeviceDefinition? FindDefinition(HidDeviceInfo d)
    {
        var defs = _definitions();
        return defs.Where(x => x.Source == "learned").FirstOrDefault(x => x.Match.Matches(d))
               ?? defs.FirstOrDefault(x => x.Match.Matches(d));
    }

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

    /// <summary>入力方式の変更など、全部作り直す。</summary>
    public void Restart()
    {
        lock (_lock)
        {
            foreach (var r in _readers.Values) r.Dispose();
            _readers.Clear();
            _lastParsed.Clear();
            _initSent.Clear();
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
        HidDeviceInfo? active;
        string? defName = null;

        lock (_lock)
        {
            _devices = devices;
            var present = devices.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // いなくなったデバイスの後始末
            foreach (var path in _parsers.Keys.Where(p => !present.Contains(p)).ToList()) _parsers.Remove(path);
            foreach (var path in _readers.Keys.Where(p => !present.Contains(p)).ToList())
            {
                _readers[path].Dispose();
                _readers.Remove(path);
            }
            foreach (var path in _lastParsed.Keys.Where(p => !present.Contains(p)).ToList()) _lastParsed.Remove(path);
            _initSent.RemoveWhere(p => !present.Contains(p));

            // 定義に一致するデバイス
            foreach (var d in devices)
            {
                var def = FindDefinition(d);
                if (def == null)
                {
                    _parsers.Remove(d.Path);
                    continue;
                }
                if (!_parsers.TryGetValue(d.Path, out var existing) || existing.Definition != def)
                    _parsers[d.Path] = new DeviceParser(def);
                if (d.UsagePage != 0) toRegister.Add((d.UsagePage, d.Usage));
            }

            if (_activePath != null && !_parsers.ContainsKey(_activePath)) _activePath = null;
            _activePath ??= _parsers.Keys.FirstOrDefault();
            active = _activePath == null ? null : devices.FirstOrDefault(d => d.Path.Equals(_activePath, StringComparison.OrdinalIgnoreCase));
            if (_activePath != null) defName = _parsers[_activePath].Definition.Name;

            // 診断中のデバイスも登録
            var diag = DiagnosticPath == null ? null : devices.FirstOrDefault(d => d.Path.Equals(DiagnosticPath, StringComparison.OrdinalIgnoreCase));
            if (diag != null && diag.UsagePage != 0 && diag.IsCandidate) toRegister.Add((diag.UsagePage, diag.Usage));

            wasConnected = _connected;
            nowConnected = _parsers.Count > 0;
            _connected = nowConnected;
        }

        // Raw Input の登録（ウィンドウのスレッドで）。既に登録したものも含めて渡す。
        var list = toRegister.Distinct().ToList();
        _window?.Invoke(() =>
        {
            lock (_lock) foreach (var c in list) _registered.Add(c);
            _window.Register(list);
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

        if (wasConnected != nowConnected)
        {
            _log.Info(nowConnected ? $"コントローラー接続: {active?.Product} ({active?.VidPid}) 定義={defName}" : "コントローラー切断");
            ConnectionChanged?.Invoke(new ConnectionChange(nowConnected, active, defName));
        }
        DevicesChanged?.Invoke();
    }

    /// <summary>Raw Input で解析できるレポートが届いていない一致デバイスは、直接読み取りに切り替える。</summary>
    private void CheckFallback()
    {
        if (_settings().InputMethod != "auto") return;
        List<HidDeviceInfo> targets;
        lock (_lock)
        {
            if (_lastParsed.Count > 0) return; // どれかから届いていれば十分
            targets = _devices.Where(d => _parsers.ContainsKey(d.Path) && !_readers.ContainsKey(d.Path)).ToList();
        }
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
        var diag = DiagnosticPath;
        if (diag != null && path.Equals(diag, StringComparison.OrdinalIgnoreCase))
            RawReportReceived?.Invoke(new RawReport(path, data, now));

        DeviceParser? parser;
        lock (_lock)
        {
            // 直接読み取り中のデバイスは Raw Input 側を無視（二重処理しない）
            if (!fromReader && _readers.ContainsKey(path)) return;
            if (!_parsers.TryGetValue(path, out parser)) return;
        }
        var state = new ControllerState { TimestampMs = now };
        if (!parser.TryParse(data, state)) return;
        lock (_lock)
        {
            _lastParsed[path] = now;
            _activePath = path;
        }
        Interlocked.Increment(ref _reports);
        StateReceived?.Invoke(state);
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
        }
        _window?.Dispose();
        _window = null;
    }

    public void Dispose() => Stop();
}
