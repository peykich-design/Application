using System.ComponentModel;
using System.Windows;
using PadMapper.App.Services;
using PadMapper.App.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadMapper.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly PageProvider _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        Icon = IconFactory.WindowIcon();
        RootNavigation.SetPageProviderService(_pages);
        Loaded += (_, _) =>
        {
            ApplyTheme(AppHost.Current.Settings.Theme);
            RootNavigation.Navigate(typeof(HomePage));
        };
    }

    public void NavigateTo(Type pageType) => RootNavigation.Navigate(pageType);

    /// <summary>テーマ："system" は OS に追従する。</summary>
    public void ApplyTheme(string theme)
    {
        SystemThemeWatcher.UnWatch(this);
        switch (theme)
        {
            case "light":
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;
            case "dark":
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
            default:
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                break;
        }
    }

    /// <summary>閉じるボタンでは終了しない（終了はトレイのメニューから）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AppHost.Current.SmokeTest && Application.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
        {
            e.Cancel = true;
            // 設定により、トレイにしまう（タスクバーから消える）か、タスクバーに最小化する
            if (AppHost.Current.Settings.CloseAction == "taskbar") WindowState = WindowState.Minimized;
            else Hide();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>ページは1つずつ作って使い回す。</summary>
    private sealed class PageProvider : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, object> _cache = new();

        public object? GetPage(Type pageType)
        {
            if (!_cache.TryGetValue(pageType, out var page))
            {
                page = Activator.CreateInstance(pageType)!;
                _cache[pageType] = page;
            }
            return page;
        }
    }
}
