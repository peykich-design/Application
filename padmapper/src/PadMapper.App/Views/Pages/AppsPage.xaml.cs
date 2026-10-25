using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadMapper.App.Services;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views.Pages;

/// <summary>アプリごとのプロファイル自動切り替え（ルールは上から順に評価）。</summary>
public partial class AppsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private List<AppRule> _rules = new();
    private AppRule? _rule;
    private bool _loading;
    private bool _dirty;

    private AppHost Host => AppHost.Current;

    public AppsPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => RefreshCurrent();
        Loaded += (_, _) =>
        {
            Load();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Load()
    {
        _loading = true;
        var profiles = Host.Profiles.ToList();
        BindProfileCombo.ItemsSource = profiles;
        BindProfileCombo.SelectedItem = profiles.FirstOrDefault(p => p.Type == ProfileType.Game) ?? profiles.FirstOrDefault();
        DefaultCombo.ItemsSource = profiles;
        DefaultCombo.SelectedItem = profiles.FirstOrDefault(p => p.Id == Host.Settings.DefaultProfileId);
        RuleProfileCombo.ItemsSource = profiles;
        NotifyCheck.IsChecked = Host.Settings.NotifyOnSwitch;
        if (!_dirty)
            _rules = Host.Settings.Rules.Select(Clone).ToList();
        _loading = false;
        RefreshList(_rule);
        RefreshCurrent();
    }

    private static AppRule Clone(AppRule r) => new()
    {
        Enabled = r.Enabled,
        ProfileId = r.ProfileId,
        Match = new AppMatch { Exe = r.Match.Exe, TitleRegex = r.Match.TitleRegex, Path = r.Match.Path },
    };

    private void RefreshCurrent()
    {
        var fg = Host.Foreground.LastExternal;
        CurrentText.Text = string.IsNullOrEmpty(fg.ExeName) ? "-" : fg.ExeName;
        CurrentPath.Text = fg.ExePath;
    }

    private string Describe(AppRule r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Match.Exe)) parts.Add(r.Match.Exe!);
        if (!string.IsNullOrWhiteSpace(r.Match.TitleRegex)) parts.Add($"{Loc.T("Apps_Title")}: {r.Match.TitleRegex}");
        if (!string.IsNullOrWhiteSpace(r.Match.Path)) parts.Add($"{Loc.T("Apps_Path")}: {r.Match.Path}");
        var cond = parts.Count == 0 ? Loc.T("Apps_EmptyRule") : string.Join(" / ", parts);
        return $"{(r.Enabled ? "" : "（" + Loc.T("Apps_Disabled") + "）")}{cond} → {Host.ProfileName(r.ProfileId)}";
    }

    private void RefreshList(AppRule? select)
    {
        _loading = true;
        RuleList.Items.Clear();
        foreach (var r in _rules) RuleList.Items.Add(new ListBoxItem { Content = Describe(r), Tag = r });
        RuleList.SelectedItem = RuleList.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.Tag == select)
                                ?? RuleList.Items.Cast<ListBoxItem>().FirstOrDefault();
        _loading = false;
        _rule = (RuleList.SelectedItem as ListBoxItem)?.Tag as AppRule;
        PopulateEditor();
    }

    private void OnRuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _rule = (RuleList.SelectedItem as ListBoxItem)?.Tag as AppRule;
        PopulateEditor();
    }

    private void PopulateEditor()
    {
        RuleEditor.IsEnabled = _rule != null;
        _loading = true;
        EnabledCheck.IsChecked = _rule?.Enabled ?? false;
        ExeBox.Text = _rule?.Match.Exe ?? "";
        TitleBox.Text = _rule?.Match.TitleRegex ?? "";
        PathBox.Text = _rule?.Match.Path ?? "";
        RuleProfileCombo.SelectedItem = Host.FindProfile(_rule?.ProfileId);
        RuleErrors.Text = _rule == null ? "" : string.Join("\n", ProfileValidator.ValidateRule(_rule));
        _loading = false;
    }

    private void OnRuleEdited(object sender, RoutedEventArgs e)
    {
        if (_loading || _rule == null) return;
        _rule.Enabled = EnabledCheck.IsChecked == true;
        _rule.Match.Exe = Empty(ExeBox.Text);
        _rule.Match.TitleRegex = Empty(TitleBox.Text);
        _rule.Match.Path = Empty(PathBox.Text);
        if (RuleProfileCombo.SelectedItem is Profile p) _rule.ProfileId = p.Id;
        RuleErrors.Text = string.Join("\n", ProfileValidator.ValidateRule(_rule));
        if (RuleList.SelectedItem is ListBoxItem item) item.Content = Describe(_rule);
        SetDirty(true);
    }

    private static string? Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void SetDirty(bool d)
    {
        _dirty = d;
        DirtyText.Text = d ? Loc.T("Profiles_Unsaved") : "";
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var fg = Host.Foreground.LastExternal;
        var r = new AppRule
        {
            Match = new AppMatch { Exe = string.IsNullOrEmpty(fg.ExeName) ? "game.exe" : fg.ExeName },
            ProfileId = (BindProfileCombo.SelectedItem as Profile)?.Id ?? Host.Settings.DefaultProfileId,
        };
        _rules.Add(r);
        RefreshList(r);
        SetDirty(true);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_rule == null) return;
        _rules.Remove(_rule);
        RefreshList(null);
        SetDirty(true);
    }

    private void Move(int delta)
    {
        if (_rule == null) return;
        int i = _rules.IndexOf(_rule);
        int j = i + delta;
        if (j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        RefreshList(_rule);
        SetDirty(true);
    }

    private void OnUp(object sender, RoutedEventArgs e) => Move(-1);
    private void OnDown(object sender, RoutedEventArgs e) => Move(1);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Host.Settings.Rules = _rules.Select(Clone).ToList();
        Host.SaveSettings();
        SetDirty(false);
    }

    private void OnBindNow(object sender, RoutedEventArgs e)
    {
        if (BindProfileCombo.SelectedItem is not Profile p) return;
        if (_dirty) OnSave(sender, e);
        var rule = Host.BindLastApp(p.Id);
        if (rule == null)
        {
            System.Windows.MessageBox.Show(Loc.T("Apps_NoApp"), "PadMapper");
            return;
        }
        _rules = Host.Settings.Rules.Select(Clone).ToList();
        RefreshList(_rules.FirstOrDefault(r => r.Match.Exe == rule.Match.Exe));
    }

    private void OnDefaultChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefaultCombo.SelectedItem is not Profile p) return;
        Host.Settings.DefaultProfileId = p.Id;
        Host.SaveSettings();
    }

    private void OnNotifyClick(object sender, RoutedEventArgs e)
    {
        Host.Settings.NotifyOnSwitch = NotifyCheck.IsChecked == true;
        Host.SaveSettings();
    }
}
