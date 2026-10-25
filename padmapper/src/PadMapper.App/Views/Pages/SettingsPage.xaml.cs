using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PadMapper.App.Services;
using PadMapper.App.Views.Controls;
using PadMapper.Core.Output;

namespace PadMapper.App.Views.Pages;

public partial class SettingsPage : Page
{
    private AppHost Host => AppHost.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _loading = true;
        var s = Host.Settings;
        GeneralPanel.Children.RemoveRange(1, GeneralPanel.Children.Count - 1);
        GeneralPanel.Children.Add(Form.Combo("Settings_Language", new[] { ("Lang_Ja", "ja"), ("Lang_En", "en") }, s.Language, v =>
        {
            s.Language = v;
            Loc.Apply(v);
            Host.SaveSettings();
        }));
        GeneralPanel.Children.Add(Form.Combo("Settings_Theme", new[] { ("Theme_System", "system"), ("Theme_Light", "light"), ("Theme_Dark", "dark") }, s.Theme, v =>
        {
            s.Theme = v;
            Host.MainWindow?.ApplyTheme(v);
            Host.SaveSettings();
        }));
        GeneralPanel.Children.Add(Form.Combo("Settings_CloseAction",
            new[] { ("Close_Tray", "tray"), ("Close_Taskbar", "taskbar") }, s.CloseAction, v => { s.CloseAction = v; Host.SaveSettings(); }));
        GeneralPanel.Children.Add(Form.Label("Settings_TrayHint", hint: true));
        var trayButton = new Button { Margin = new Thickness(0, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
        trayButton.SetResourceReference(ContentProperty, "Settings_OpenTaskbarSettings");
        trayButton.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); } catch { }
        };
        GeneralPanel.Children.Add(trayButton);
        GeneralPanel.Children.Add(Form.Check("Settings_UiNav", s.ControllerUiNavigation, v => { s.ControllerUiNavigation = v; Host.SaveSettings(); }));
        GeneralPanel.Children.Add(Form.Check("Settings_LogTitles", s.LogWindowTitles, v => { s.LogWindowTitles = v; Host.SaveSettings(); }));
        GeneralPanel.Children.Add(Form.Combo("Settings_InputMethod",
            new[] { ("Method_Auto", "auto"), ("Method_RawInput", "rawInput"), ("Method_HidRead", "hidRead") }, s.InputMethod, v =>
            {
                s.InputMethod = v;
                Host.SaveSettings();
                Host.Controllers.Restart();
            }));
        GeneralPanel.Children.Add(Form.Check("Settings_InitCommand", s.SendInitCommand, v =>
        {
            s.SendInitCommand = v;
            Host.SaveSettings();
            Host.Controllers.Restart();
        }));
        GeneralPanel.Children.Add(Form.Label("Settings_InitCommandHint", hint: true));

        HotkeyBox.Text = s.PauseHotkey;
        HotkeyState.Text = Host.HotkeyRegistered ? "" : Loc.T("Diag_HotkeyFailed");
        DataPath.Text = Host.Store.BaseDir;
        VersionText.Text = $"PadMapper {typeof(SettingsPage).Assembly.GetName().Version}";
        _loading = false;
        RefreshStartup();
    }

    private void RefreshStartup()
    {
        Task.Run(() =>
        {
            var state = StartupTask.Query(out var cmd);
            Dispatcher.BeginInvoke(() =>
            {
                _loading = true;
                StartupSwitch.IsChecked = state is StartupTask.TaskState.Registered or StartupTask.TaskState.RegisteredOtherPath;
                StartupState.Text = state switch
                {
                    StartupTask.TaskState.Registered => Loc.T("Diag_TaskRegistered"),
                    StartupTask.TaskState.RegisteredOtherPath => Loc.F("Diag_TaskOtherPath", cmd),
                    StartupTask.TaskState.NotRegistered => Loc.T("Diag_TaskNotRegistered"),
                    _ => Loc.T("Diag_TaskUnknown"),
                };
                _loading = false;
            });
        });
    }

    private void OnStartupClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool enable = StartupSwitch.IsChecked == true;
        bool ok = StartupTask.SetEnabled(enable, Host.Log);
        if (!ok) System.Windows.MessageBox.Show(Loc.T("Settings_StartupFailed"), "PadMapper", MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (enable && !StartupTask.IsElevated &&
                 System.Windows.MessageBox.Show(Loc.T("Settings_RestartElevated"), "PadMapper", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            // タスク経由で管理者として起動し直す
            ((App)Application.Current).RestartViaTask();
        }
        RefreshStartup();
    }

    private void OnApplyHotkey(object sender, RoutedEventArgs e)
    {
        if (!Hotkey.TryParse(HotkeyBox.Text, out var hk))
        {
            HotkeyState.Text = Loc.T("Settings_HotkeyInvalid");
            return;
        }
        Host.Settings.PauseHotkey = hk.ToString();
        Host.SaveSettings();
        HotkeyBox.Text = hk.ToString();
        // 登録は入力スレッドで行うので、少し待ってから結果を表示する
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(300);
            HotkeyState.Text = Host.HotkeyRegistered ? Loc.T("Common_Ok") : Loc.T("Diag_HotkeyFailed");
        });
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Host.Store.BaseDir}\"") { UseShellExecute = true }); } catch { }
    }
}
