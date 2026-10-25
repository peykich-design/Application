using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PadMapper.App.Input;
using PadMapper.App.Services;
using PadMapper.Core.Devices;
using PadMapper.Core.Diagnostics;
using PadMapper.Core.Engine;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views.Pages;

/// <summary>
/// 診断（Phase 0）：接続状態・権限・自動起動の状態、HID デバイス一覧、生レポートの HEX ダンプと差分、
/// 「ボタンを押して変化したビットを割り当てる」ウィザード。
/// </summary>
public partial class DiagnosticsPage : Page
{
    private sealed record DeviceRow(HidDeviceInfo Info, string Product, string VidPid, string Usage, string Connection, string Length, string Definition);

    private enum Phase { None, Idle, Active }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, TextBlock> _status = new();
    private readonly object _lock = new();
    private readonly MappingLearner _learner = new();
    private readonly Queue<string> _history = new();
    private HidDeviceInfo? _device;
    private HidReader? _directReader;
    private bool _dumping;
    private byte[]? _latest;
    private byte[]? _shown;
    private byte[]? _idleSample;
    private long _received;
    private long _lastReceived;
    private DateTime _rateTime = DateTime.UtcNow;
    private Phase _phase;
    private DateTime _idleUntil;
    private DeviceDefinition? _def;
    private string? _taskStateText;

    private AppHost Host => AppHost.Current;

    private static readonly string[] Targets =
        ReportMarkdown.RequiredButtons.Append("Plus")
            .Concat(ReportMarkdown.RequiredAxes)
            .Concat(new[] { "gyro.pitch", "gyro.yaw", "gyro.roll", "accel.x", "accel.y", "accel.z" })
            .ToArray();

    public DiagnosticsPage()
    {
        InitializeComponent();
        foreach (var key in new[] { "Diag_Elevation", "Diag_Task", "Diag_Method", "Diag_Device", "Diag_Definition", "Diag_LastReport", "Diag_ReportCount", "Diag_InitCommand", "Diag_Hotkey" })
            AddStatusRow(key);
        TargetCombo.ItemsSource = Targets;
        TargetCombo.SelectedIndex = 0;
        FormatCombo.ItemsSource = new[] { "u8", "s8", "u16", "s16" };
        FormatCombo.SelectedIndex = 0;
        _timer.Tick += (_, _) => Tick();
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Loaded += (_, _) =>
        {
            Host.Controllers.DevicesChanged += OnDevicesChanged;
            Host.Controllers.RawReportReceived += OnRawReport;
            RefreshDevices();
            RefreshStatus();
            UpdateTaskState();
            _timer.Start();
            _statusTimer.Start();
            UpdateWizardButtons();
        };
        Unloaded += (_, _) =>
        {
            Host.Controllers.DevicesChanged -= OnDevicesChanged;
            Host.Controllers.RawReportReceived -= OnRawReport;
            StopDump();
            _timer.Stop();
            _statusTimer.Stop();
        };
    }

    // ---------- 状態 ----------

    private void AddStatusRow(string key)
    {
        int row = StatusGrid.RowDefinitions.Count;
        StatusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new TextBlock { Margin = new Thickness(0, 2, 8, 2) };
        label.SetResourceReference(TextBlock.TextProperty, key);
        label.SetResourceReference(StyleProperty, "Hint");
        var value = new TextBlock { Margin = new Thickness(0, 2, 0, 2) };
        value.SetResourceReference(StyleProperty, "Label");
        Grid.SetRow(label, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        StatusGrid.Children.Add(label);
        StatusGrid.Children.Add(value);
        _status[key] = value;
    }

    private void UpdateTaskState()
    {
        // schtasks の呼び出しは少し時間がかかるので別スレッドで
        Task.Run(() =>
        {
            var state = StartupTask.Query(out var cmd);
            var text = state switch
            {
                StartupTask.TaskState.Registered => Loc.T("Diag_TaskRegistered"),
                StartupTask.TaskState.RegisteredOtherPath => Loc.F("Diag_TaskOtherPath", cmd),
                StartupTask.TaskState.NotRegistered => Loc.T("Diag_TaskNotRegistered"),
                _ => Loc.T("Diag_TaskUnknown"),
            };
            Dispatcher.BeginInvoke(() =>
            {
                _taskStateText = text;
                RefreshStatus();
            });
        });
    }

    private void RefreshStatus()
    {
        var c = Host.Controllers;
        _status["Diag_Elevation"].Text = StartupTask.IsElevated ? Loc.T("Home_Elevated") : Loc.T("Home_NotElevated");
        _status["Diag_Task"].Text = _taskStateText ?? "…";
        _status["Diag_Method"].Text = $"{Host.Settings.InputMethod} / {c.ActiveMethod}";
        var active = c.ActiveCollections;
        _status["Diag_Device"].Text = active.Count == 0 ? Loc.T("Home_NotConnected")
            : string.Join("\n", active.Select(a => $"{a.device.Product} ({a.device.VidPid}, {a.device.UsageText}, {a.device.Connection})"));
        var def = c.ActiveDefinition;
        _status["Diag_Definition"].Text = def == null ? "-"
            : $"{def.Name} — {(def.Source == "learned" ? Loc.T("Diag_SourceLearned") : Loc.T("Diag_SourceBuiltin"))}{(def.Verified ? "" : " / " + Loc.T("Diag_Unverified"))}";
        _status["Diag_LastReport"].Text = c.LastParsedMs is double ms ? $"{(c.NowMs - ms) / 1000.0:0.0} s" : Loc.T("Home_NoReports");
        _status["Diag_ReportCount"].Text = c.ReportCount.ToString(CultureInfo.InvariantCulture);
        _status["Diag_InitCommand"].Text = Host.Settings.SendInitCommand
            ? (c.InitCommandSent ? Loc.T("Diag_InitSent") : Loc.T("Diag_InitEnabledNotSent"))
            : Loc.T("Diag_InitNotSent");
        _status["Diag_Hotkey"].Text = $"{Host.Settings.PauseHotkey} — {(Host.HotkeyRegistered ? Loc.T("Common_Ok") : Loc.T("Diag_HotkeyFailed"))}";
        ErrorList.ItemsSource = Host.Log.RecentProblems.Reverse().Select(e => $"{e.Time:HH:mm:ss} {e.Level}: {e.Message}").ToList();
    }

    // ---------- デバイス ----------

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(RefreshDevices);

    private void RefreshDevices()
    {
        var selected = _device?.Path;
        var rows = Host.Controllers.Devices
            .Where(d => ShowAllCheck.IsChecked == true || d.IsCandidate)
            .OrderByDescending(d => Host.Controllers.FindDefinition(d) != null)
            .ThenBy(d => d.Product)
            .Select(d => new DeviceRow(d, string.IsNullOrEmpty(d.Product) ? "(?)" : d.Product, d.VidPid, d.UsageText, d.Connection,
                d.InputReportLength.ToString(), Host.Controllers.FindDefinition(d)?.Name ?? ""))
            .ToList();
        DeviceList.ItemsSource = rows;
        DeviceList.SelectedItem = rows.FirstOrDefault(r => r.Info.Path == selected);
    }

    private void OnShowAllClick(object sender, RoutedEventArgs e) => RefreshDevices();

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        Host.Controllers.ScheduleRefresh(0);
        UpdateTaskState();
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow row || row.Info.Path == _device?.Path) return;
        StopDump();
        _device = row.Info;
        SelectedText.Text = $"{row.Product} — {row.VidPid} — Usage {row.Usage} — {row.Connection}\n{row.Info.Path}";
        lock (_lock)
        {
            _learner.Reset();
            _phase = Phase.None;
            _idleSample = null;
        }
        var existing = Host.Definitions.FirstOrDefault(d => d.Source == "learned" && d.Match.Matches(row.Info));
        _def = existing != null ? CloneDef(existing) : NewDefinition(row.Info);
        ShowDefinition();
        UpdateWizardButtons();
    }

    // ---------- ダンプ ----------

    private void OnToggleDump(object sender, RoutedEventArgs e)
    {
        if (_dumping) StopDump();
        else StartDump();
    }

    private void StartDump()
    {
        if (_device == null) return;
        _dumping = true;
        _received = _lastReceived = 0;
        _latest = _shown = null;
        _history.Clear();
        Host.Controllers.AddDiagnostic(_device);
        if (DirectCheck.IsChecked == true) _directReader = Host.Controllers.OpenDiagnosticReader(_device);
        DumpButton.Content = Loc.T("Diag_StopDump");
        UpdateWizardButtons();
    }

    private void StopDump()
    {
        _dumping = false;
        if (_device != null) Host.Controllers.RemoveDiagnostic(_device.Path);
        _directReader?.Dispose();
        _directReader = null;
        DumpButton.Content = Loc.T("Diag_StartDump");
        lock (_lock) _phase = Phase.None;
        UpdateWizardButtons();
    }

    /// <summary>入力スレッドから呼ばれる。</summary>
    private void OnRawReport(RawReport r)
    {
        if (!_dumping || _device == null || !r.DevicePath.Equals(_device.Path, StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock)
        {
            _latest = r.Data;
            _received++;
            if (_phase == Phase.Idle)
            {
                _learner.AddIdle(r.Data);
                _idleSample ??= r.Data;
            }
            else if (_phase == Phase.Active) _learner.AddActive(r.Data);
        }
    }

    private void Tick()
    {
        byte[]? latest;
        long received;
        Phase phase;
        int idleCount, activeCount;
        lock (_lock)
        {
            latest = _latest;
            received = _received;
            phase = _phase;
            idleCount = _learner.IdleCount;
            activeCount = _learner.ActiveCount;
            if (_phase == Phase.Idle && DateTime.UtcNow >= _idleUntil) _phase = phase = Phase.None;
        }

        var now = DateTime.UtcNow;
        if ((now - _rateTime).TotalSeconds >= 1)
        {
            double rate = (received - _lastReceived) / (now - _rateTime).TotalSeconds;
            _lastReceived = received;
            _rateTime = now;
            RateText.Text = _dumping ? Loc.F("Diag_Rate", rate.ToString("0"), received) : "";
        }

        if (phase == Phase.Idle) WizardStatus.Text = Loc.F("Diag_RecordingIdle", idleCount);
        else if (phase == Phase.Active) WizardStatus.Text = Loc.F("Diag_RecordingActive", activeCount);
        UpdateWizardButtons();

        if (latest == null || ReferenceEquals(latest, _shown)) return;
        RenderHex(latest, _shown);
        if (_shown == null || !latest.AsSpan().SequenceEqual(_shown))
        {
            _history.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {ReportDiff.ToHex(latest)}");
            while (_history.Count > 30) _history.Dequeue();
            HistoryBox.Text = string.Join("\n", _history.Reverse());
        }
        _shown = latest;
    }

    /// <summary>HEX を表示し、前回から変わったバイトを色付きにする。変化したビットも一覧にする。</summary>
    private void RenderHex(byte[] cur, byte[]? prev)
    {
        var idx = new StringBuilder();
        for (int i = 0; i < cur.Length; i++) idx.Append(i.ToString("D2")).Append(' ');
        IndexText.Text = idx.ToString();

        HexText.Inlines.Clear();
        var accent = (Brush)(TryFindResource("SystemFillColorCautionBrush") ?? Brushes.Orange);
        for (int i = 0; i < cur.Length; i++)
        {
            bool changed = prev != null && (i >= prev.Length || prev[i] != cur[i]);
            var run = new Run(cur[i].ToString("X2") + " ");
            if (changed)
            {
                run.Foreground = accent;
                run.FontWeight = FontWeights.Bold;
            }
            HexText.Inlines.Add(run);
        }
        if (prev != null)
        {
            var bits = ReportDiff.ChangedBitList(prev, cur).Take(24).Select(b => $"[{b.offset}].{b.bit}").ToList();
            if (bits.Count > 0) BitsText.Text = Loc.T("Diag_ChangedBits") + " " + string.Join(" ", bits);
        }
    }

    // ---------- ウィザード ----------

    private void UpdateWizardButtons()
    {
        bool ready = _dumping && _device != null;
        IdleButton.IsEnabled = ready && _phase == Phase.None;
        ActiveButton.IsEnabled = ready && _phase == Phase.None && _learner.IdleCount >= 2;
        AssignButton.IsEnabled = ready && _phase == Phase.Active;
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        var t = TargetCombo.SelectedItem as string ?? "";
        FormatCombo.IsEnabled = ReportMarkdown.RequiredAxes.Contains(t);
        ScaleBox.IsEnabled = t.StartsWith("gyro.") || t.StartsWith("accel.");
        ScaleBox.Text = t.StartsWith("accel.") ? "0.000244" : "0.061";
    }

    private void OnRecordIdle(object sender, RoutedEventArgs e)
    {
        lock (_lock)
        {
            _learner.Reset();
            _idleSample = null;
            _phase = Phase.Idle;
            _idleUntil = DateTime.UtcNow.AddSeconds(2);
        }
        WizardStatus.Text = Loc.F("Diag_RecordingIdle", 0);
    }

    private void OnRecordActive(object sender, RoutedEventArgs e)
    {
        lock (_lock)
        {
            _learner.ResetActive();
            _phase = Phase.Active;
        }
        WizardStatus.Text = Loc.F("Diag_RecordingActive", 0);
    }

    private void OnAssign(object sender, RoutedEventArgs e)
    {
        if (_def == null) return;
        var target = TargetCombo.SelectedItem as string ?? "";
        string result;
        lock (_lock)
        {
            _phase = Phase.None;
            // 先頭バイトが 0 以外ならレポート ID とみなす（レポート ID のないデバイスは 0）
            if (_def.ReportId == null && _idleSample is { Length: > 0 } s && s[0] != 0) _def.ReportId = s[0];
            if (_idleSample != null) _def.MinLength = Math.Max(_def.MinLength, _idleSample.Length);
            result = Assign(target);
        }
        WizardStatus.Text = result;
        ShowDefinition();
        // 次の項目へ
        if (TargetCombo.SelectedIndex < Targets.Length - 1 && !result.StartsWith("✗")) TargetCombo.SelectedIndex++;
    }

    private string Assign(string target)
    {
        var def = _def!;
        // 他の項目に使っている位置は候補から外す（同じバイトの別ビットは許す）
        if (target.StartsWith("gyro.") || target.StartsWith("accel."))
        {
            var axis = target.Split('.')[1];
            double scale = double.TryParse(ScaleBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) ? sc : 0.061;
            var used = def.Axes.SelectMany(a => new[] { a.Offset, a.OffsetHi ?? a.Offset + 1 })
                .Concat(def.Buttons.Select(b => b.Offset)).ToHashSet();
            var list = target.StartsWith("gyro.") ? def.Gyro : def.Accel;
            foreach (var g in list.Where(g => !g.Axis.Equals(axis, StringComparison.OrdinalIgnoreCase)))
            {
                used.Add(g.Offset);
                used.Add(g.OffsetHi ?? g.Offset + 1);
            }
            var m = _learner.LearnSensor(axis, scale, used);
            if (m == null) return Loc.F("Diag_AssignFailed", target);
            list.RemoveAll(g => g.Axis.Equals(axis, StringComparison.OrdinalIgnoreCase));
            list.Add(m);
            return Loc.F("Diag_Assigned", target, $"[{m.Offset}],[{m.OffsetHi}] s16");
        }
        if (ReportMarkdown.RequiredAxes.Contains(target))
        {
            var used = def.Gyro.Concat(def.Accel).SelectMany(g => new[] { g.Offset, g.OffsetHi ?? g.Offset + 1 })
                .Concat(def.Axes.Where(a => a.Axis != target).Select(a => a.Offset)).ToHashSet();
            var m = _learner.LearnAxis(target, FormatCombo.SelectedItem as string ?? "u8", used);
            if (m == null) return Loc.F("Diag_AssignFailed", target);
            def.Axes.RemoveAll(a => a.Axis.Equals(target, StringComparison.OrdinalIgnoreCase));
            def.Axes.Add(m);
            return Loc.F("Diag_Assigned", target, $"[{m.Offset}] {m.Format} center={m.Center} min={m.Min} max={m.Max}");
        }
        var usedAxes = def.Axes.Select(a => a.Offset)
            .Concat(def.Gyro.Concat(def.Accel).SelectMany(g => new[] { g.Offset, g.OffsetHi ?? g.Offset + 1 })).ToHashSet();
        var b = _learner.LearnButton(target, usedAxes);
        if (b == null) return Loc.F("Diag_AssignFailed", target);
        def.Buttons.RemoveAll(x => x.Input.Equals(target, StringComparison.OrdinalIgnoreCase));
        def.Buttons.Add(b);
        return Loc.F("Diag_Assigned", target, $"[{b.Offset}] mask=0x{b.Mask:X2}{(b.Value is int v ? $" value=0x{v:X2}" : "")}");
    }

    private static DeviceDefinition NewDefinition(HidDeviceInfo d) => new()
    {
        Id = $"learned-{d.VendorId:x4}{d.ProductId:x4}-{d.UsagePage:x4}{d.Usage:x2}",
        Name = (string.IsNullOrEmpty(d.Product) ? d.VidPid : d.Product) + " (learned)",
        Source = "learned",
        Match = new DeviceMatch { VendorId = d.VendorId, ProductId = d.ProductId, UsagePage = d.UsagePage, Usage = d.Usage },
    };

    private static DeviceDefinition CloneDef(DeviceDefinition d) => ProfileJson.Deserialize<DeviceDefinition>(ProfileJson.Serialize(d));

    private void ShowDefinition()
    {
        if (_def == null)
        {
            DefinitionBox.Text = "";
            MissingText.Text = "";
            return;
        }
        DefinitionBox.Text = ProfileJson.Serialize(_def);
        var missing = ReportMarkdown.Missing(_def).ToList();
        MissingText.Text = missing.Count == 0 ? Loc.T("Diag_AllFound") : Loc.F("Diag_Missing", string.Join(", ", missing));
        VerifiedCheck.IsChecked = _def.Verified;
    }

    private void OnVerifiedClick(object sender, RoutedEventArgs e)
    {
        if (_def == null) return;
        _def.Verified = VerifiedCheck.IsChecked == true;
        ShowDefinition();
    }

    private void OnSaveDefinition(object sender, RoutedEventArgs e)
    {
        if (_def == null) return;
        Host.SaveDefinition(_def);
        WizardStatus.Text = Loc.T("Diag_DefinitionSaved");
        RefreshDevices();
    }

    private void OnExportMarkdown(object sender, RoutedEventArgs e)
    {
        if (_def == null) return;
        var md = ReportMarkdown.Build(_def, _device, _idleSample == null ? null : ReportDiff.ToHex(_idleSample));
        try { Clipboard.SetText(md); } catch { }
        var dlg = new SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = "vader4pro-report.md" };
        if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, md, new UTF8Encoding(false));
        WizardStatus.Text = Loc.T("Diag_MarkdownCopied");
    }

    private void OnStartFromBuiltin(object sender, RoutedEventArgs e)
    {
        if (_device == null) return;
        var def = BuiltInDevices.Vader4ProV1();
        def.Id = NewDefinition(_device).Id;
        def.Name = (string.IsNullOrEmpty(_device.Product) ? _device.VidPid : _device.Product) + " (learned)";
        def.Source = "learned";
        def.Verified = false;
        def.Match = NewDefinition(_device).Match;
        _def = def;
        ShowDefinition();
    }

    private void OnStartOver(object sender, RoutedEventArgs e)
    {
        if (_device == null) return;
        _def = NewDefinition(_device);
        lock (_lock)
        {
            _learner.Reset();
            _idleSample = null;
        }
        ShowDefinition();
    }

    private void OnDeleteLearned(object sender, RoutedEventArgs e)
    {
        if (_def == null) return;
        if (System.Windows.MessageBox.Show(Loc.T("Diag_AskDeleteLearned"), "PadMapper", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        Host.Store.DeleteDeviceDefinition(_def.Id);
        Host.ReloadDefinitions();
        Host.Controllers.Restart();
        if (_device != null) _def = NewDefinition(_device);
        ShowDefinition();
        RefreshDevices();
    }

    /// <summary>不具合の報告用に、デバイス一覧・使用中の定義・直近のレポートをテキストにしてコピーする。</summary>
    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        var c = Host.Controllers;
        var sb = new StringBuilder();
        sb.AppendLine($"PadMapper {typeof(DiagnosticsPage).Assembly.GetName().Version} / {Environment.OSVersion} / admin={StartupTask.IsElevated} / method={Host.Settings.InputMethod}");
        sb.AppendLine($"connected={c.IsConnected} reports={c.ReportCount} needsSetup={c.NeedsSetup}");
        sb.AppendLine();
        foreach (var d in c.Devices.Where(d => d.IsCandidate).OrderBy(d => d.VidPid))
        {
            var def = c.FindDefinition(d);
            sb.AppendLine($"- {d.Product} | {d.Manufacturer} | {d.VidPid} | usage {d.UsageText} | {d.Connection} | in={d.InputReportLength} out={d.OutputReportLength} | def={def?.Name ?? "-"} ({def?.Source})");
            if (c.LastReport(d.Path) is { } r)
                sb.AppendLine($"  last ({(c.NowMs - r.time) / 1000:0.0}s ago): {ReportDiff.ToHex(r.data)}");
            else
                sb.AppendLine("  last: (no report)");
            sb.AppendLine($"  {d.Path}");
        }
        sb.AppendLine();
        foreach (var p in Host.Log.RecentProblems) sb.AppendLine($"{p.Time:HH:mm:ss} {p.Level}: {p.Message}");
        try
        {
            Clipboard.SetText(sb.ToString());
            System.Windows.MessageBox.Show(Loc.T("Diag_ReportCopied"), "PadMapper");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "PadMapper");
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(Host.Log.Directory);

    private void OnOpenData(object sender, RoutedEventArgs e) => OpenFolder(Host.Store.BaseDir);

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }
}
