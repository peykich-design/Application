using System.Windows.Forms;

namespace PadMapper.App.Services;

/// <summary>タスクトレイのアイコンとメニュー（WinForms の NotifyIcon）。</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly System.Drawing.Icon _normal = IconFactory.AppIcon(SystemInformation.SmallIconSize.Width);
    private readonly System.Drawing.Icon _paused = IconFactory.Create(true, SystemInformation.SmallIconSize.Width);
    private readonly AppHost _host;

    public TrayIcon(AppHost host)
    {
        _host = host;
        _icon = new NotifyIcon
        {
            Icon = _normal,
            Text = "PadMapper",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _host.ShowMainWindow();
        };
        BuildMenu();
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(Loc.T("Tray_Show"), null, (_, _) => _host.ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());

        var paused = _host.Engine.IsPaused;
        menu.Items.Add(paused ? Loc.T("Tray_Resume") : Loc.T("Tray_Pause"), null, (_, _) => _host.Engine.TogglePause());
        var gyro = new ToolStripMenuItem(Loc.T("Tray_Gyro")) { Checked = _host.Engine.IsGyroEnabled };
        gyro.Click += (_, _) => _host.Engine.ToggleGyro();
        menu.Items.Add(gyro);

        var profiles = new ToolStripMenuItem(Loc.T("Tray_Profiles"));
        foreach (var p in _host.Profiles.ToList())
        {
            var id = p.Id;
            var item = new ToolStripMenuItem(p.Name) { Checked = id == _host.ActiveProfileId };
            item.Click += (_, _) => _host.SwitchProfileManually(id);
            profiles.DropDownItems.Add(item);
        }
        menu.Items.Add(profiles);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray_Exit"), null, (_, _) => _host.Exit());
    }

    public void Update(bool paused, string profileName)
    {
        _icon.Icon = paused ? _paused : _normal;
        var text = $"PadMapper - {profileName}{(paused ? " (" + Loc.T("Status_Paused") + ")" : "")}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Notify(string title, string text)
    {
        try
        {
            _icon.ShowBalloonTip(2000, title, text, ToolTipIcon.None);
        }
        catch
        {
            // 通知が使えない環境でも続行
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normal.Dispose();
        _paused.Dispose();
    }
}
