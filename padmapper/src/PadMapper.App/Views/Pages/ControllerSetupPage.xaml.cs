using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadMapper.App.Services;
using PadMapper.Core.Devices;
using PadMapper.Core.Diagnostics;
using PadMapper.Core.Engine;

namespace PadMapper.App.Views.Pages;

/// <summary>
/// コントローラー設定：「A ボタンを押して離してください」のように1つずつ案内し、押されたら自動で次へ進む。
/// 1台のコントローラーが持つ全てのコレクション（標準のゲームパッド + ベンダー独自）を同時に見る。
/// 設定中はエンジンを一時停止し、ボタンを押してもキー入力やクリックが出ないようにする。
/// </summary>
public partial class ControllerSetupPage : Page
{
    /// <summary>VID:PID が同じコレクションをまとめた「1台のコントローラー」。</summary>
    private sealed record PhysicalDevice(string Key, string Title, List<HidDeviceInfo> Collections);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly object _lock = new();
    private GuidedMapper? _mapper;
    private PhysicalDevice? _device;
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _wasPaused;
    private double _lastInput = double.NaN;
    private double _started;

    private AppHost Host => AppHost.Current;

    public ControllerSetupPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            Host.Controllers.DevicesChanged += OnDevicesChanged;
            LoadDevices();
        };
        Unloaded += (_, _) =>
        {
            Host.Controllers.DevicesChanged -= OnDevicesChanged;
            Stop(restoreUi: false);
        };
    }

    // ---------- デバイス ----------

    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_mapper == null) LoadDevices();
    });

    private void OnRefresh(object sender, RoutedEventArgs e) => Host.Controllers.ScheduleRefresh(0);

    private void LoadDevices()
    {
        var c = Host.Controllers;
        var groups = c.Devices.Where(d => d.IsCandidate)
            .GroupBy(d => d.VidPid)
            .Select(g =>
            {
                var list = g.ToList();
                var name = list.Select(d => d.Product).FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? "(?)";
                return new PhysicalDevice(g.Key, $"{name}  ({g.Key})", list);
            })
            // 標準のゲームパッドを持つ機器を先に
            .OrderByDescending(p => p.Collections.Any(d => c.IsHidGamepad(d.Path)))
            .ThenBy(p => p.Title)
            .ToList();
        var selected = (DeviceCombo.SelectedItem as PhysicalDevice)?.Key ?? c.ActiveDevice?.VidPid;
        DeviceCombo.ItemsSource = groups;
        DeviceCombo.SelectedItem = groups.FirstOrDefault(g => g.Key == selected) ?? groups.FirstOrDefault();
        NoDeviceText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = groups.Count > 0;
        UpdateDeviceDetail();
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e) => UpdateDeviceDetail();

    private void UpdateDeviceDetail()
    {
        if (DeviceCombo.SelectedItem is not PhysicalDevice p)
        {
            DeviceDetail.Text = "";
            return;
        }
        var c = Host.Controllers;
        DeviceDetail.Text = string.Join("\n", p.Collections.Select(d =>
        {
            var kind = c.IsHidGamepad(d.Path) ? Loc.T("Setup_KindGamepad") : Loc.T("Setup_KindVendor");
            var def = c.FindDefinition(d);
            return $"・{kind} — Usage {d.UsageText} — {d.Connection} — {Loc.T("Diag_ColDefinition")}: {def?.Name ?? "-"}";
        }));
    }

    // ---------- 開始・終了 ----------

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not PhysicalDevice p) return;
        Stop(restoreUi: false);
        _device = p;
        _paths = p.Collections.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock) _mapper = new GuidedMapper(GuidedMapper.DefaultSteps(BackButtonsCheck.IsChecked == true));
        _wasPaused = Host.Engine.IsPaused;
        Host.Engine.SetPaused(true);
        foreach (var d in p.Collections) Host.Controllers.AddDiagnostic(d);
        Host.Controllers.RawReportReceived += OnRawReport;
        _started = Host.Controllers.NowMs;
        _lastInput = double.NaN;
        StepCard.Visibility = Visibility.Visible;
        DoneCard.Visibility = Visibility.Collapsed;
        ResultTitle.Visibility = ResultCard.Visibility = Visibility.Visible;
        DeviceCombo.IsEnabled = StartButton.IsEnabled = BackButtonsCheck.IsEnabled = false;
        SavedText.Text = "";
        _timer.Start();
        Render();
    }

    private void Stop(bool restoreUi)
    {
        if (_mapper == null) return;
        _timer.Stop();
        Host.Controllers.RawReportReceived -= OnRawReport;
        foreach (var path in _paths) Host.Controllers.RemoveDiagnostic(path);
        if (!_wasPaused) Host.Engine.SetPaused(false);
        lock (_lock) _mapper = null;
        if (restoreUi)
        {
            StepCard.Visibility = DoneCard.Visibility = Visibility.Collapsed;
            ResultTitle.Visibility = ResultCard.Visibility = Visibility.Collapsed;
        }
        DeviceCombo.IsEnabled = StartButton.IsEnabled = BackButtonsCheck.IsEnabled = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Stop(restoreUi: true);

    /// <summary>入力スレッドから呼ばれる。</summary>
    private void OnRawReport(RawReport r)
    {
        if (!_paths.Contains(r.DevicePath)) return;
        var c = Host.Controllers;
        HidSnapshot? snap = c.IsHidGamepad(r.DevicePath) ? c.ParseHid(r.DevicePath, r.Data) : null;
        lock (_lock)
        {
            if (_mapper == null) return;
            _lastInput = r.TimestampMs;
            if (snap != null) _mapper.FeedHid(r.DevicePath, snap, r.TimestampMs);
            else if (!c.IsHidGamepad(r.DevicePath)) _mapper.FeedRaw(r.DevicePath, r.Data, r.TimestampMs);
        }
    }

    // ---------- 表示 ----------

    private void Tick()
    {
        lock (_lock) _mapper?.Tick(Host.Controllers.NowMs);
        Render();
    }

    private void Render()
    {
        GuidedMapper? m;
        lock (_lock) m = _mapper;
        if (m == null) return;
        SetupStep? step;
        int index, total;
        bool waiting, finished;
        string? candidate;
        List<string> results;
        lock (_lock)
        {
            step = m.Current;
            index = m.Index;
            total = m.Steps.Count;
            waiting = m.IsWaitingRelease;
            finished = m.IsFinished;
            candidate = m.CandidateDescription;
            results = m.Assignments.Select(a => $"✓ {InputName(a.Step.Target)} → {a.Description}{SourceSuffix(a.Source)}").ToList();
        }

        ResultList.ItemsSource = results;
        if (finished)
        {
            StepCard.Visibility = Visibility.Collapsed;
            DoneCard.Visibility = Visibility.Visible;
            _timer.Stop();
            return;
        }

        ProgressText.Text = Loc.F("Setup_Progress", index + 1, total);
        Progress.Maximum = total;
        Progress.Value = index;
        PromptText.Text = Prompt(step!);
        SubText.Text = step!.Kind switch
        {
            SetupStepKind.Rest => Loc.T("Setup_RestHint"),
            SetupStepKind.Button => Loc.T("Setup_ButtonHint"),
            _ => Loc.T("Setup_AxisHint"),
        };
        DetectText.Text = waiting ? Loc.F("Setup_Detected", candidate) : "";

        double now = Host.Controllers.NowMs;
        bool noInput = double.IsNaN(_lastInput) ? now - _started > 2000 : false;
        NoInputText.Visibility = noInput ? Visibility.Visible : Visibility.Collapsed;
    }

    private string SourceSuffix(string path)
    {
        // コレクションが複数ある機器では、どちらのレポートかを添える
        if (_device == null || _device.Collections.Count <= 1) return "";
        return Host.Controllers.IsHidGamepad(path) ? $"（{Loc.T("Setup_KindGamepad")}）" : $"（{Loc.T("Setup_KindVendor")}）";
    }

    private static string InputName(string target) => target switch
    {
        "LX" => Loc.T("Setup_LeftStick") + " ←→",
        "LY" => Loc.T("Setup_LeftStick") + " ↑↓",
        "RX" => Loc.T("Setup_RightStick") + " ←→",
        "RY" => Loc.T("Setup_RightStick") + " ↑↓",
        _ => Loc.T("BtnName_" + target),
    };

    private static string Prompt(SetupStep s) => s.Kind switch
    {
        SetupStepKind.Rest => Loc.T("Setup_Rest"),
        SetupStepKind.StickRight => Loc.F("Setup_StickRight", Loc.T(s.Target.StartsWith('L') ? "Setup_LeftStick" : "Setup_RightStick")),
        SetupStepKind.StickDown => Loc.F("Setup_StickDown", Loc.T(s.Target.StartsWith('L') ? "Setup_LeftStick" : "Setup_RightStick")),
        SetupStepKind.Trigger => Loc.F("Setup_Trigger", Loc.T("BtnName_" + s.Target)),
        _ => Loc.F("Setup_Button", Loc.T("BtnName_" + s.Target)),
    };

    // ---------- 操作 ----------

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        lock (_lock) _mapper?.Skip();
        Render();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        lock (_lock) _mapper?.Back();
        Render();
    }

    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (_mapper == null)
        {
            OnStart(sender, e);
            return;
        }
        lock (_lock) _mapper.Restart();
        StepCard.Visibility = Visibility.Visible;
        DoneCard.Visibility = Visibility.Collapsed;
        SavedText.Text = "";
        _timer.Start();
        Render();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_mapper == null || _device == null) return;
        var defs = new List<DeviceDefinition>();
        lock (_lock)
        {
            foreach (var source in _mapper.SourcesWithAssignments())
            {
                var info = _device.Collections.FirstOrDefault(d => d.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
                if (info == null) continue;
                // ベンダー独自のレポートは、組み込みの定義（ジャイロなど）を元にして上書きする
                var baseDef = Host.Controllers.FindDefinition(info);
                if (baseDef != null && BuiltInDevices.IsGeneric(baseDef)) baseDef = null;
                defs.Add(_mapper.BuildDefinition(source, info, baseDef));
            }
        }
        if (defs.Count == 0)
        {
            SavedText.Text = Loc.T("Setup_NothingToSave");
            return;
        }
        Host.SaveDefinitions(defs);
        Stop(restoreUi: false);
        SavedText.Text = Loc.T("Setup_Saved");
    }
}
