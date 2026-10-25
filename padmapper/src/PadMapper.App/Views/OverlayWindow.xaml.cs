using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadMapper.App.Native;
using PadMapper.App.Services;
using PadMapper.Core.Engine;
using PadMapper.Core.Profiles;

namespace PadMapper.App.Views;

/// <summary>
/// コントローラーだけで設定を変えるためのオーバーレイ。最前面に出るが、フォーカスは奪わない
/// （WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW）。排他フルスクリーンのゲームの上には表示できない。
/// 操作に使うボタンの入力はゲームにも届くため、既定では背面ボタンを使う。
/// </summary>
public partial class OverlayWindow : Window
{
    private sealed record Item(string Text, Action Run, bool CloseAfter);

    private readonly AppHost _host;
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<Item> _items = new();
    private int _index;

    internal OverlayWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _autoClose.Tick += (_, _) => Close2();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TOPMOST;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        };
    }

    public bool IsOpen => IsVisible;

    public void Toggle()
    {
        if (IsVisible) Close2();
        else Open();
    }

    public void Open()
    {
        Build();
        _index = 0;
        Render();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 40;
        Show();
        _host.Engine.SetOverlayOpen(true);
        _autoClose.Stop();
        _autoClose.Start();
    }

    /// <summary>非表示にする（Window.Close は使わず、使い回す）。</summary>
    public void Close2()
    {
        _autoClose.Stop();
        Hide();
        _host.Engine.SetOverlayOpen(false);
    }

    public void Navigate(OverlayNav nav)
    {
        if (!IsVisible) return;
        _autoClose.Stop();
        _autoClose.Start();
        switch (nav)
        {
            case OverlayNav.Up: _index = (_index - 1 + _items.Count) % _items.Count; break;
            case OverlayNav.Down: _index = (_index + 1) % _items.Count; break;
            case OverlayNav.Select: Execute(_index); return;
            case OverlayNav.Close: Close2(); return;
        }
        Render();
    }

    private void Execute(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        var item = _items[i];
        item.Run();
        if (item.CloseAfter) Close2();
        else
        {
            Build();
            Render();
        }
    }

    private void Build()
    {
        var items = new List<Item>();
        foreach (var p in _host.Profiles.ToList())
        {
            var id = p.Id;
            var mark = id == _host.ActiveProfileId ? "● " : "○ ";
            items.Add(new Item(mark + p.Name, () => _host.SwitchProfileManually(id), true));
        }
        var active = _host.FindProfile(_host.ActiveProfileId);
        if (active != null)
        {
            items.Add(new Item(Loc.F("Overlay_SensUp", active.Gyro.SensitivityX.ToString("0.00")), () => ChangeSensitivity(1.1), false));
            items.Add(new Item(Loc.F("Overlay_SensDown", active.Gyro.SensitivityX.ToString("0.00")), () => ChangeSensitivity(1 / 1.1), false));
        }
        items.Add(new Item(_host.Engine.IsGyroEnabled ? Loc.T("Overlay_GyroOff") : Loc.T("Overlay_GyroOn"), () => _host.Engine.ToggleGyro(), false));
        items.Add(new Item(_host.Engine.IsPaused ? Loc.T("Overlay_Resume") : Loc.T("Overlay_Pause"), () => _host.Engine.TogglePause(), true));
        items.Add(new Item(Loc.T("Overlay_OpenSettings"), () => _host.ShowMainWindow(), true));
        items.Add(new Item(Loc.T("Overlay_CloseMenu"), () => { }, true));
        _items = items;
        if (_index >= _items.Count) _index = 0;

        var o = active?.Overlay ?? new OverlaySettings();
        StatusText.Text = Loc.F("Overlay_Status", _host.ProfileName(_host.ActiveProfileId),
            _host.Engine.IsPaused ? Loc.T("Status_Paused") : Loc.T("Status_Running"));
        HelpText.Text = Loc.F("Overlay_Help", o.Up, o.Down, o.Select, o.Close);
    }

    /// <summary>使用中プロファイルのジャイロ感度を変えて保存する。</summary>
    private void ChangeSensitivity(double factor)
    {
        var p = _host.FindProfile(_host.ActiveProfileId)?.DeepClone();
        if (p == null) return;
        p.Gyro.SensitivityX = Math.Round(Math.Clamp(p.Gyro.SensitivityX * factor, 0.05, 10), 2);
        p.Gyro.SensitivityY = Math.Round(Math.Clamp(p.Gyro.SensitivityY * factor, 0.05, 10), 2);
        _host.SaveProfile(p);
    }

    private void Render()
    {
        Items.Items.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var selected = i == _index;
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Background = selected ? new SolidColorBrush(Color.FromRgb(0, 120, 212)) : Brushes.Transparent,
                Child = new TextBlock { Text = _items[i].Text, Foreground = Brushes.White, FontSize = 15 },
                Cursor = Cursors.Hand,
            };
            border.MouseLeftButtonUp += (_, _) => Execute(index);
            Items.Items.Add(border);
        }
    }
}
