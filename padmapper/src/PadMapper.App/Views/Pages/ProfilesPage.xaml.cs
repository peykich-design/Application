using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PadMapper.App.Services;
using PadMapper.App.Views;
using PadMapper.App.Views.Controls;
using PadMapper.Core.Model;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views.Pages;

/// <summary>
/// プロファイルの編集。コントローラー図でボタンを選び、そのボタンの割り当て（バインディング）を編集する。
/// 編集は作業用のコピーに対して行い、「保存」で反映する。
/// </summary>
public partial class ProfilesPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Profile? _edit;
    private bool _dirty;
    private bool _loading;
    private LogicalInput _input = LogicalInput.A;
    private Binding? _binding;

    private AppHost Host => AppHost.Current;

    private static readonly TriggerKind[] TriggerKinds = Enum.GetValues<TriggerKind>();
    private static readonly ActionType[] Actions = Enum.GetValues<ActionType>();
    private static readonly MouseButton[] Buttons = Enum.GetValues<MouseButton>();

    public ProfilesPage()
    {
        InitializeComponent();
        InputCombo.ItemsSource = LogicalInputs.All.Select(i => i.ToString()).ToList();
        KeyCombo.ItemsSource = KeyCatalog.Names;
        FillCombo(TriggerCombo, TriggerKinds.Select(t => "Trig_" + t));
        FillCombo(ActionCombo, Actions.Select(a => "Act_" + a));
        FillCombo(MouseCombo, Buttons.Select(b => "Mouse_" + b));
        Diagram.InputClicked += SelectInput;
        _timer.Tick += (_, _) => Diagram.SetPressed(Host.Engine.Snapshot.Inputs);

        Loaded += (_, _) =>
        {
            Host.ProfilesChanged += OnProfilesChanged;
            _timer.Start();
            if (_edit == null) ReloadCombo(Host.ActiveProfileId);
        };
        Unloaded += (_, _) =>
        {
            Host.ProfilesChanged -= OnProfilesChanged;
            _timer.Stop();
        };
    }

    private static void FillCombo(ComboBox combo, IEnumerable<string> keys)
    {
        combo.Items.Clear();
        foreach (var k in keys)
        {
            var item = new ComboBoxItem();
            item.SetResourceReference(ContentControl.ContentProperty, k);
            combo.Items.Add(item);
        }
    }

    // ---------- プロファイルの選択 ----------

    private void OnProfilesChanged()
    {
        if (!_dirty) ReloadCombo(_edit?.Id);
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
        if (_loading || ProfileCombo.SelectedItem is not Profile p || p.Id == _edit?.Id) return;
        if (_dirty)
        {
            var r = System.Windows.MessageBox.Show(Loc.F("Profiles_AskSave", _edit!.Name), "PadMapper",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel)
            {
                _loading = true;
                ProfileCombo.SelectedItem = Host.FindProfile(_edit.Id);
                _loading = false;
                return;
            }
            if (r == MessageBoxResult.Yes) Host.SaveProfile(_edit.DeepClone());
        }
        LoadEdit(p.DeepClone());
    }

    private void LoadEdit(Profile p)
    {
        _edit = p;
        _loading = true;
        NameBox.Text = p.Name;
        TypeCombo.SelectedIndex = p.Type == ProfileType.Game ? 1 : 0;
        _loading = false;
        BuildSettingsPanels();
        SelectInput(_input);
        SetDirty(false);
        UpdateTypeHint();
        UpdateIssues();
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        DirtyText.Text = dirty ? Loc.T("Profiles_Unsaved") : "";
    }

    private void MarkDirty()
    {
        if (_loading) return;
        SetDirty(true);
        UpdateIssues();
    }

    private void UpdateIssues()
    {
        if (_edit == null) return;
        var ids = Host.Profiles.Select(p => p.Id).Append(_edit.Id);
        IssueList.ItemsSource = ProfileValidator.Validate(_edit, ids)
            .Select(i => (i.Severity == IssueSeverity.Error ? "⛔ " : "⚠ ") + i.Message + (i.BindingIndex >= 0 ? $"（{_edit.Bindings.ElementAtOrDefault(i.BindingIndex)?.Input}）" : ""))
            .ToList();
        Diagram.SetBound(_edit.Bindings.Select(b => LogicalInputs.TryParse(b.Input, out var li) ? (LogicalInput?)li : null)
            .Where(x => x != null).Select(x => x!.Value));
    }

    private void UpdateTypeHint()
    {
        bool game = _edit?.Type == ProfileType.Game;
        TypeHint.Text = Loc.T(game ? "Profiles_GameHint" : "Profiles_DesktopHint");
        SticksGameHint.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _edit == null) return;
        _edit.Name = NameBox.Text;
        MarkDirty();
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _edit == null) return;
        _edit.Type = TypeCombo.SelectedIndex == 1 ? ProfileType.Game : ProfileType.Desktop;
        UpdateTypeHint();
        UpdateEditorVisibility();
        MarkDirty();
    }

    // ---------- 入力とバインディング ----------

    private void SelectInput(LogicalInput input)
    {
        _input = input;
        Diagram.Selected = input;
        _loading = true;
        InputCombo.SelectedItem = input.ToString();
        _loading = false;
        SelectedInputText.Text = Loc.F("Profiles_BindingsFor", input);
        RefreshBindingList(null);
    }

    private void OnInputComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || InputCombo.SelectedItem is not string s || !LogicalInputs.TryParse(s, out var li)) return;
        SelectInput(li);
    }

    private IEnumerable<Binding> BindingsForInput() =>
        _edit?.Bindings.Where(b => LogicalInputs.TryParse(b.Input, out var li) && li == _input) ?? Enumerable.Empty<Binding>();

    private void RefreshBindingList(Binding? select)
    {
        BindingList.Items.Clear();
        foreach (var b in BindingsForInput())
            BindingList.Items.Add(new ListBoxItem { Content = Summary(b), Tag = b });
        var target = BindingList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == select) ?? BindingList.Items.Cast<ListBoxItem>().FirstOrDefault();
        BindingList.SelectedItem = target;
        if (target == null)
        {
            _binding = null;
            EditorPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string Summary(Binding b)
    {
        var trig = Loc.T("Trig_" + b.Trigger);
        if (b.Trigger == TriggerKind.ModeShift) return $"{trig} → {Loc.T("Profiles_Layer")}: {b.Layer}";
        var a = b.Action;
        string what = a.Type switch
        {
            ActionType.Key => KeyText(a.Key, a.Modifiers),
            ActionType.MouseButton => Loc.T("Mouse_" + (a.Button ?? MouseButton.Left)),
            ActionType.Scroll => $"{Loc.T("Act_Scroll")} {a.Amount}",
            ActionType.MouseMove => $"{Loc.T("Act_MouseMove")} {a.Dx},{a.Dy}",
            ActionType.Macro => $"{Loc.T("Act_Macro")}: {a.Macro}",
            ActionType.Profile => $"{Loc.T("Act_Profile")}: {a.Id}",
            _ => Loc.T("Act_" + a.Type),
        };
        var extra = b.Trigger == TriggerKind.Chord && b.Chord != null ? $" (+{string.Join("+", b.Chord)})" : "";
        var layer = !string.IsNullOrEmpty(b.Layer) ? $" [{b.Layer}]" : "";
        return $"{trig}{extra}{layer} → {what}";
    }

    /// <summary>"Ctrl + Tab（次のタブ）" のようにプリセット名も添えて表示する。</summary>
    private static string KeyText(string? key, List<string>? modifiers)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        var text = KeyPickerWindow.Describe(key, modifiers);
        var preset = ShortcutPresets.Find(key, modifiers);
        return preset != null ? $"{Loc.T(preset.NameKey)}（{text}）" : text;
    }

    /// <summary>仮想キーボード・よく使う操作からキーを選ぶ。</summary>
    private void OnPickKey(object sender, RoutedEventArgs e)
    {
        if (_binding == null) return;
        var picker = new KeyPickerWindow(_binding.Action.Key, _binding.Action.Modifiers) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedKey == null) return;
        var mods = picker.SelectedModifiers;
        _loading = true;
        KeyCombo.Text = picker.SelectedKey;
        ModCtrl.IsChecked = mods.Contains("LCtrl");
        ModShift.IsChecked = mods.Contains("LShift");
        ModAlt.IsChecked = mods.Contains("LAlt");
        ModWin.IsChecked = mods.Contains("LWin");
        _loading = false;
        OnEditorChanged(sender, e);
    }

    private void OnBindingSelected(object sender, SelectionChangedEventArgs e)
    {
        _binding = (BindingList.SelectedItem as ListBoxItem)?.Tag as Binding;
        PopulateEditor();
    }

    private void OnAddBinding(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        var b = new Binding { Input = _input.ToString(), Action = ActionDef.KeyPress("F13") };
        _edit.Bindings.Add(b);
        RefreshBindingList(b);
        MarkDirty();
    }

    private void OnRemoveBinding(object sender, RoutedEventArgs e)
    {
        if (_edit == null || _binding == null) return;
        _edit.Bindings.Remove(_binding);
        RefreshBindingList(null);
        MarkDirty();
    }

    private void PopulateEditor()
    {
        if (_binding == null)
        {
            EditorPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EditorPanel.Visibility = Visibility.Visible;
        _loading = true;
        var b = _binding;
        var a = b.Action;
        TriggerCombo.SelectedIndex = Array.IndexOf(TriggerKinds, b.Trigger);
        HoldBox.Text = b.HoldMs?.ToString() ?? "";
        DoubleBox.Text = b.DoubleMs?.ToString() ?? "";
        ChordBox.Text = b.Chord == null ? "" : string.Join(", ", b.Chord);
        LayerBox.Text = b.Layer ?? "";
        ActionCombo.SelectedIndex = Array.IndexOf(Actions, a.Type);
        KeyCombo.Text = a.Key ?? "";
        KeyDescription.Text = a.Type == ActionType.Key ? KeyText(a.Key, a.Modifiers) : "";
        var mods = (a.Modifiers ?? new()).Select(m => KeyCatalog.Normalize(m) ?? m).ToList();
        ModCtrl.IsChecked = mods.Contains("LCtrl") || mods.Contains("RCtrl");
        ModShift.IsChecked = mods.Contains("LShift") || mods.Contains("RShift");
        ModAlt.IsChecked = mods.Contains("LAlt") || mods.Contains("RAlt");
        ModWin.IsChecked = mods.Contains("LWin") || mods.Contains("RWin");
        MouseCombo.SelectedIndex = Array.IndexOf(Buttons, a.Button ?? MouseButton.Left);
        ScrollBox.Text = (a.Amount ?? 1).ToString(CultureInfo.InvariantCulture);
        HorizontalCheck.IsChecked = a.Horizontal == true;
        DxBox.Text = (a.Dx ?? 0).ToString();
        DyBox.Text = (a.Dy ?? 0).ToString();
        MacroBox.Text = a.Macro ?? "";
        var targets = Host.Profiles.Select(p => p.Id).Append("next").ToList();
        TargetProfileCombo.ItemsSource = targets;
        TargetProfileCombo.SelectedItem = a.Id;
        _loading = false;
        UpdateEditorVisibility();
    }

    private void UpdateEditorVisibility()
    {
        if (_binding == null) return;
        static Visibility V(bool b) => b ? Visibility.Visible : Visibility.Collapsed;
        var t = _binding.Trigger;
        var a = _binding.Action.Type;
        bool shift = t == TriggerKind.ModeShift;
        HoldPanel.Visibility = V(t is TriggerKind.Hold or TriggerKind.Tap);
        DoublePanel.Visibility = V(t == TriggerKind.Double);
        ChordPanel.Visibility = V(t == TriggerKind.Chord);
        LayerPanel.Visibility = V(_edit?.Type == ProfileType.Desktop || !string.IsNullOrEmpty(_binding.Layer));
        ActionCombo.Visibility = V(!shift);
        KeyPanel.Visibility = V(!shift && a == ActionType.Key);
        MouseCombo.Visibility = V(!shift && a == ActionType.MouseButton);
        ScrollPanel.Visibility = V(!shift && a == ActionType.Scroll);
        MovePanel.Visibility = V(!shift && a == ActionType.MouseMove);
        MacroPanel.Visibility = V(!shift && a == ActionType.Macro);
        TargetProfileCombo.Visibility = V(!shift && a == ActionType.Profile);
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _binding == null) return;
        var b = _binding;
        if (TriggerCombo.SelectedIndex >= 0) b.Trigger = TriggerKinds[TriggerCombo.SelectedIndex];
        b.HoldMs = int.TryParse(HoldBox.Text, out var hold) ? hold : null;
        b.DoubleMs = int.TryParse(DoubleBox.Text, out var dbl) ? dbl : null;
        var chord = ChordBox.Text.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        b.Chord = b.Trigger == TriggerKind.Chord && chord.Count > 0 ? chord : null;
        b.Layer = string.IsNullOrWhiteSpace(LayerBox.Text) ? null : LayerBox.Text.Trim();

        var type = ActionCombo.SelectedIndex >= 0 ? Actions[ActionCombo.SelectedIndex] : ActionType.None;
        var a = new ActionDef { Type = type };
        switch (type)
        {
            case ActionType.Key:
                a.Key = KeyCatalog.Normalize(KeyCombo.Text) ?? KeyCombo.Text;
                var mods = new List<string>();
                if (ModCtrl.IsChecked == true) mods.Add("LCtrl");
                if (ModShift.IsChecked == true) mods.Add("LShift");
                if (ModAlt.IsChecked == true) mods.Add("LAlt");
                if (ModWin.IsChecked == true) mods.Add("LWin");
                a.Modifiers = mods.Count > 0 ? mods : null;
                break;
            case ActionType.MouseButton:
                a.Button = MouseCombo.SelectedIndex >= 0 ? Buttons[MouseCombo.SelectedIndex] : MouseButton.Left;
                break;
            case ActionType.Scroll:
                a.Amount = double.TryParse(ScrollBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amt) ? amt : 1;
                a.Horizontal = HorizontalCheck.IsChecked == true ? true : null;
                break;
            case ActionType.MouseMove:
                a.Dx = int.TryParse(DxBox.Text, out var dx) ? dx : 0;
                a.Dy = int.TryParse(DyBox.Text, out var dy) ? dy : 0;
                break;
            case ActionType.Macro:
                a.Macro = MacroBox.Text;
                break;
            case ActionType.Profile:
                a.Id = TargetProfileCombo.SelectedItem as string;
                break;
        }
        b.Action = a;
        KeyDescription.Text = a.Type == ActionType.Key ? KeyText(a.Key, a.Modifiers) : "";

        if (BindingList.SelectedItem is ListBoxItem item) item.Content = Summary(b);
        UpdateEditorVisibility();
        MarkDirty();
    }

    // ---------- スティック・トリガー・オーバーレイ ----------

    private void BuildSettingsPanels()
    {
        if (_edit == null) return;
        var p = _edit;
        BuildStick(LeftStickPanel, p.Sticks.Left);
        BuildStick(RightStickPanel, p.Sticks.Right);

        Reset(TriggerPanel);
        TriggerPanel.Children.Add(Form.Slider("Profiles_TriggerThreshold", 0.05, 1, p.Triggers.Threshold, 0.05, "0.00", v => { p.Triggers.Threshold = v; MarkDirty(); }));
        TriggerPanel.Children.Add(Form.Slider("Profiles_TriggerSecond", 0.5, 1, p.Triggers.SecondStage, 0.01, "0.00", v => { p.Triggers.SecondStage = v; MarkDirty(); }));
        TriggerPanel.Children.Add(Form.Slider("Profiles_DirectionThreshold", 0.2, 0.95, p.Sticks.DirectionThreshold, 0.05, "0.00", v => { p.Sticks.DirectionThreshold = v; MarkDirty(); }));

        Reset(OverlayPanel);
        var inputs = LogicalInputs.PhysicalButtons.Select(i => i.ToString()).ToList();
        var o = p.Overlay;
        o.Trigger ??= new Binding { Input = "M4", Trigger = TriggerKind.Hold, HoldMs = 600 };
        OverlayPanel.Children.Add(Form.Choice("Profiles_OverlayOpen", inputs, o.Trigger.Input, v => { o.Trigger!.Input = v; MarkDirty(); }));
        OverlayPanel.Children.Add(Form.Number("Profiles_HoldMs", o.Trigger.EffectiveHoldMs, v => { o.Trigger!.HoldMs = (int)v; MarkDirty(); }));
        OverlayPanel.Children.Add(Form.Choice("Overlay_Up", inputs, o.Up, v => { o.Up = v; MarkDirty(); }));
        OverlayPanel.Children.Add(Form.Choice("Overlay_Down", inputs, o.Down, v => { o.Down = v; MarkDirty(); }));
        OverlayPanel.Children.Add(Form.Choice("Overlay_Select", inputs, o.Select, v => { o.Select = v; MarkDirty(); }));
        OverlayPanel.Children.Add(Form.Choice("Overlay_Close", inputs, o.Close, v => { o.Close = v; MarkDirty(); }));
    }

    private static void Reset(Panel panel)
    {
        // 先頭（見出し）だけ残す
        while (panel.Children.Count > 1) panel.Children.RemoveAt(panel.Children.Count - 1);
    }

    private void BuildStick(Panel panel, StickSettings s)
    {
        Reset(panel);
        panel.Children.Add(Form.Combo("Profiles_StickMode",
            new[] { ("Stick_None", StickMode.None), ("Stick_Mouse", StickMode.Mouse), ("Stick_Scroll", StickMode.Scroll) },
            s.Mode, v => { s.Mode = v; MarkDirty(); }));
        panel.Children.Add(Form.Slider("Profiles_Deadzone", 0, 0.5, s.Deadzone, 0.01, "0.00", v => { s.Deadzone = v; MarkDirty(); }));
        panel.Children.Add(Form.Slider("Profiles_Curve", 0.5, 4, s.Curve, 0.1, "0.0", v => { s.Curve = v; MarkDirty(); }));
        panel.Children.Add(Form.Number("Profiles_Speed", s.Speed, v => { s.Speed = v; MarkDirty(); }));
        panel.Children.Add(Form.Slider("Profiles_ScrollMinSpeed", 0, 10, s.MinSpeed, 0.5, "0.0", v => { s.MinSpeed = v; MarkDirty(); }));
        panel.Children.Add(Form.Check("Profiles_SmoothScroll", s.SmoothScroll, v => { s.SmoothScroll = v; MarkDirty(); }));
        panel.Children.Add(Form.Check("Profiles_InvertY", s.InvertY, v => { s.InvertY = v; MarkDirty(); }));
        panel.Children.Add(Form.Label("Profiles_SpeedHint", hint: true));
    }

    // ---------- ボタン ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        if (string.IsNullOrWhiteSpace(_edit.Name)) _edit.Name = _edit.Id;
        Host.SaveProfile(_edit.DeepClone());
        SetDirty(false);
        _loading = true;
        ProfileCombo.ItemsSource = Host.Profiles.ToList();
        ProfileCombo.SelectedItem = Host.FindProfile(_edit.Id);
        _loading = false;
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        var original = Host.FindProfile(_edit.Id);
        if (original != null) LoadEdit(original.DeepClone());
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        var p = new Profile
        {
            Id = ProfileStore.UniqueId("profile", Host.Profiles.Select(x => x.Id)),
            Name = Loc.T("Profiles_NewName"),
            Type = ProfileType.Desktop,
        };
        Host.SaveProfile(p);
        ReloadCombo(p.Id);
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        var p = _edit.DeepClone();
        p.Id = ProfileStore.UniqueId(_edit.Id, Host.Profiles.Select(x => x.Id));
        p.Name = Loc.F("Profiles_CopyName", _edit.Name);
        Host.SaveProfile(p);
        SetDirty(false);
        ReloadCombo(p.Id);
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        if (Host.Profiles.Count <= 1)
        {
            System.Windows.MessageBox.Show(Loc.T("Profiles_CannotDeleteLast"), "PadMapper");
            return;
        }
        if (System.Windows.MessageBox.Show(Loc.F("Profiles_AskDelete", _edit.Name), "PadMapper", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        Host.DeleteProfile(_edit.Id);
        SetDirty(false);
        ReloadCombo(Host.ActiveProfileId);
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var p = Host.Store.Import(dlg.FileName, Host.Profiles.Select(x => x.Id));
            Host.SaveProfile(p);
            ReloadCombo(p.Id);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(Loc.F("Profiles_ImportFailed", ex.Message), "PadMapper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = ProfileStore.SafeFileName(_edit.Id) + ".json" };
        if (dlg.ShowDialog() != true) return;
        Host.Store.Export(_edit, dlg.FileName);
    }

    private void OnUseNow(object sender, RoutedEventArgs e)
    {
        if (_edit == null) return;
        if (_dirty) OnSave(sender, e);
        Host.SwitchProfileManually(_edit.Id);
    }
}
