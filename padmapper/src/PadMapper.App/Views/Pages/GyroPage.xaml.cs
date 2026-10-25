using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadMapper.App.Services;
using PadMapper.App.Views.Controls;
using PadMapper.Core.Model;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views.Pages;

/// <summary>ジャイロの設定と、生値・補正後の値のリアルタイム表示。</summary>
public partial class GyroPage : Page
{
    private const int History = 240;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Queue<(Vector3 raw, Vector2 outDps)> _samples = new();
    private readonly Polyline[] _lines;
    private Profile? _edit;
    private bool _dirty;
    private bool _loading;
    private long _lastCount = -1;

    private AppHost Host => AppHost.Current;

    public GyroPage()
    {
        InitializeComponent();
        _lines = new[] { "#FF4FC3F7", "#FFFFB74D", "#FF81C784", "#FFE57373" }
            .Select(c => new Polyline { Stroke = (Brush)new BrushConverter().ConvertFromString(c)!, StrokeThickness = 1.5 })
            .ToArray();
        foreach (var l in _lines) Chart.Children.Add(l);
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            if (_edit == null) ReloadCombo(Host.ActiveProfileId);
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void ReloadCombo(string? selectId)
    {
        _loading = true;
        var list = Host.Profiles.ToList();
        ProfileCombo.ItemsSource = list;
        var sel = list.FirstOrDefault(p => p.Id == selectId) ?? list.FirstOrDefault();
        ProfileCombo.SelectedItem = sel;
        _loading = false;
        if (sel != null) LoadEdit(sel.DeepClone());
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not Profile p) return;
        if (_dirty && _edit != null &&
            System.Windows.MessageBox.Show(Loc.F("Profiles_AskSave", _edit.Name), "PadMapper", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Save();
        LoadEdit(p.DeepClone());
    }

    private void LoadEdit(Profile p)
    {
        _edit = p;
        _dirty = false;
        DirtyText.Text = "";
        Build();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        DirtyText.Text = Loc.T("Profiles_Unsaved");
    }

    private void Build()
    {
        while (SettingsPanel.Children.Count > 1) SettingsPanel.Children.RemoveAt(SettingsPanel.Children.Count - 1);
        if (_edit == null) return;
        var g = _edit.Gyro;
        var inputs = LogicalInputs.All.Select(i => i.ToString()).ToList();
        void Add(UIElement e) => SettingsPanel.Children.Add(e);

        Add(Form.Check("Gyro_Enabled", g.Enabled, v => { g.Enabled = v; MarkDirty(); }));
        Add(Form.Combo("Gyro_Activation",
            new[] { ("Gyro_Always", GyroActivationMode.Always), ("Gyro_WhileHeld", GyroActivationMode.WhileHeld), ("Gyro_Toggle", GyroActivationMode.Toggle) },
            g.Activation.Mode, v => { g.Activation.Mode = v; MarkDirty(); }));
        Add(Form.Choice("Gyro_ActivationInput", inputs, g.Activation.Input ?? "LT", v => { g.Activation.Input = v; MarkDirty(); }));
        Add(Form.Combo("Gyro_Axis",
            new[] { ("Gyro_AxisYaw", GyroAxis.Yaw), ("Gyro_AxisRoll", GyroAxis.Roll), ("Gyro_AxisPlayer", GyroAxis.Player) },
            g.Axis, v => { g.Axis = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_SensX", 0.05, 10, g.SensitivityX, 0.05, "0.00", v => { g.SensitivityX = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_SensY", 0.05, 10, g.SensitivityY, 0.05, "0.00", v => { g.SensitivityY = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_Deadzone", 0, 10, g.DeadzoneDps, 0.1, "0.0", v => { g.DeadzoneDps = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_Smoothing", 0, 1, g.Smoothing, 0.05, "0.00", v => { g.Smoothing = v; MarkDirty(); }));
        Add(Form.Check("Gyro_InvertX", g.InvertX, v => { g.InvertX = v; MarkDirty(); }));
        Add(Form.Check("Gyro_InvertY", g.InvertY, v => { g.InvertY = v; MarkDirty(); }));
        Add(Form.Check("Gyro_AutoCalibrate", g.AutoCalibrate, v => { g.AutoCalibrate = v; MarkDirty(); }));
        Add(Form.Check("Gyro_AccelEnabled", g.Accel.Enabled, v => { g.Accel.Enabled = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_AccelLow", 0, 200, g.Accel.LowDps, 1, "0", v => { g.Accel.LowDps = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_AccelHigh", 10, 600, g.Accel.HighDps, 5, "0", v => { g.Accel.HighDps = v; MarkDirty(); }));
        Add(Form.Slider("Gyro_AccelMax", 1, 5, g.Accel.MaxMultiplier, 0.1, "0.0", v => { g.Accel.MaxMultiplier = v; MarkDirty(); }));
    }

    private void Tick()
    {
        var s = Host.Engine.Snapshot;
        if (s.StatesProcessed != _lastCount)
        {
            _lastCount = s.StatesProcessed;
            _samples.Enqueue((s.GyroRaw, s.GyroOutputDps));
            while (_samples.Count > History) _samples.Dequeue();
            DrawChart();
        }
        bool hasGyro = s.State?.HasGyro == true;
        ValuesText.Text = !hasGyro
            ? Loc.T("Gyro_NoData")
            : Loc.F("Gyro_Values",
                $"{s.GyroRaw.X,7:0.0} {s.GyroRaw.Y,7:0.0} {s.GyroRaw.Z,7:0.0}",
                $"{s.GyroCorrected.X,7:0.0} {s.GyroCorrected.Y,7:0.0} {s.GyroCorrected.Z,7:0.0}",
                $"{s.GyroBias.X,6:0.00} {s.GyroBias.Y,6:0.00} {s.GyroBias.Z,6:0.00}",
                s.GyroCalibrated ? Loc.T("Common_Yes") : Loc.T("Common_No"),
                s.GyroActive ? Loc.T("Gyro_Active") : Loc.T("Gyro_Inactive"));
    }

    private void DrawChart()
    {
        double w = Math.Max(1, Chart.ActualWidth), h = Math.Max(1, Chart.ActualHeight);
        double scale = h / 2 / 300.0; // ±300 deg/s
        var pts = new[] { new PointCollection(), new PointCollection(), new PointCollection(), new PointCollection() };
        int i = 0;
        double step = w / (History - 1);
        foreach (var (raw, o) in _samples)
        {
            double x = i++ * step;
            double Y(double v) => h / 2 - Math.Clamp(v, -300, 300) * scale;
            pts[0].Add(new Point(x, Y(raw.Y)));
            pts[1].Add(new Point(x, Y(raw.X)));
            pts[2].Add(new Point(x, Y(o.X)));
            pts[3].Add(new Point(x, Y(o.Y)));
        }
        for (int k = 0; k < 4; k++) _lines[k].Points = pts[k];
    }

    private void Save()
    {
        if (_edit == null) return;
        // ジャイロ以外の変更（別画面での編集）を消さないよう、保存済みのプロファイルにジャイロ設定だけ反映する
        var saved = Host.FindProfile(_edit.Id)?.DeepClone() ?? _edit;
        saved.Gyro = _edit.Gyro;
        Host.SaveProfile(saved);
        _dirty = false;
        DirtyText.Text = "";
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_edit != null && Host.FindProfile(_edit.Id) is { } p) LoadEdit(p.DeepClone());
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        Host.Engine.CalibrateGyro();
        System.Windows.MessageBox.Show(Loc.T("Gyro_CalibrateHint"), "PadMapper");
    }

    private void OnResetBias(object sender, RoutedEventArgs e) => Host.Engine.ResetGyroBias();
}
