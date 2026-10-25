using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadMapper.App.Services;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views.Pages;

public partial class HomePage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private AppHost Host => AppHost.Current;

    public HomePage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Host.StateChanged += Refresh;
            _timer.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Host.StateChanged -= Refresh;
            _timer.Stop();
        };
    }

    private void Refresh()
    {
        var c = Host.Controllers;
        var snap = Host.Engine.Snapshot;
        NoticeList.ItemsSource = Host.StartupNotices.ToList();

        var dev = c.ActiveDevice;
        if (c.IsConnected && dev != null)
        {
            ControllerText.Text = string.IsNullOrEmpty(dev.Product) ? dev.VidPid : dev.Product;
            var defs = string.Join(" + ", c.ActiveCollections.Select(a => a.definition.Name).Distinct());
            var age = c.LastParsedMs is double ms ? (c.NowMs - ms) / 1000.0 : (double?)null;
            ControllerDetail.Text = Loc.F("Home_ControllerDetail", dev.Connection, defs,
                c.ActiveMethod, age == null ? Loc.T("Home_NoReports") : $"{age:0.0}s");
        }
        else
        {
            ControllerText.Text = Loc.T("Home_NotConnected");
            ControllerDetail.Text = Loc.T("Home_NotConnectedHint");
        }

        bool needsSetup = !c.IsConnected || c.NeedsSetup;
        SetupCard.Visibility = needsSetup ? Visibility.Visible : Visibility.Collapsed;
        SetupText.Text = Loc.T(c.IsConnected ? "Home_SetupGeneric" : "Home_SetupNotConnected");

        ProfileText.Text = Host.ProfileName(Host.ActiveProfileId);
        var p = Host.FindProfile(Host.ActiveProfileId);
        string type = p?.Type == ProfileType.Game ? Loc.T("Type_Game") : Loc.T("Type_Desktop");
        string how = Host.IsUiNavigationActive ? Loc.T("Home_ByUiNav") : Host.IsManualOverride ? Loc.T("Home_ByManual") : Loc.T("Home_ByAuto");
        ProfileDetail.Text = $"{type} · {how}" + (snap.Layer != null ? $" · {Loc.T("Home_Layer")} {snap.Layer}" : "");

        var fg = Host.Foreground.LastExternal;
        ForegroundText.Text = string.IsNullOrEmpty(fg.ExeName) ? "-" : fg.ExeName;

        bool elevated = StartupTask.IsElevated;
        ElevationText.Text = elevated ? Loc.T("Home_Elevated") : Loc.T("Home_NotElevated");
        ElevationDetail.Text = elevated ? Loc.T("Home_ElevatedHint") : Loc.T("Home_NotElevatedHint");

        PauseSwitch.IsChecked = Host.Engine.IsPaused;
        PauseHint.Text = Loc.F("Home_PauseHint", Host.Settings.PauseHotkey);
        GyroSwitch.IsChecked = Host.Engine.IsGyroEnabled;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e) => Host.Engine.SetPaused(PauseSwitch.IsChecked == true);

    private void OnGyroClick(object sender, RoutedEventArgs e) => Host.Engine.SetGyroEnabled(GyroSwitch.IsChecked == true);

    private void OnOpenSetup(object sender, RoutedEventArgs e) => Host.MainWindow?.NavigateTo(typeof(ControllerSetupPage));

    private void OnOverlayClick(object sender, RoutedEventArgs e) => Host.Overlay?.Toggle();
}
