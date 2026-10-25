using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PadMapper.App.Input;
using PadMapper.App.Native;
using PadMapper.App.Output;
using PadMapper.App.Services;
using PadMapper.App.Views;
using PadMapper.Core.Devices;
using PadMapper.Core.Engine;
using PadMapper.Core.Logging;
using PadMapper.Core.Output;
using PadMapper.Core.Profiles;

namespace PadMapper.App;

/// <summary>
/// アプリ全体のつなぎ役。設定・プロファイル・エンジン・入力・前面アプリ監視・トレイ・オーバーレイを持ち、
/// プロファイルの自動切り替えを行う。画面（ページ）は AppHost.Current から状態を読む。
/// </summary>
internal sealed class AppHost : IDisposable
{
    public static AppHost Current { get; private set; } = null!;

    private const int PauseHotkeyId = 1;

    private readonly Dispatcher _ui;
    private List<DeviceDefinition> _definitions = new();
    private string? _profileBeforeUiNav;
    private bool _exiting;

    public FileLogger Log { get; }
    public ProfileStore Store { get; }
    public AppSettings Settings { get; private set; }
    public ObservableCollection<Profile> Profiles { get; } = new();
    public PadEngine Engine { get; private set; } = null!;
    public ControllerManager Controllers { get; private set; } = null!;
    public ForegroundWatcher Foreground { get; private set; } = null!;
    public TrayIcon? Tray { get; private set; }
    public OverlayWindow? Overlay { get; private set; }
    public MainWindow? MainWindow { get; set; }
    public bool SmokeTest { get; }

    public string ActiveProfileId { get; private set; } = "";
    public bool IsManualOverride { get; private set; }
    public bool IsUiNavigationActive { get; private set; }
    public bool HotkeyRegistered { get; private set; }
    public List<string> StartupNotices { get; } = new();

    /// <summary>プロファイル・一時停止・接続などが変わった（UI スレッドで呼ばれる）。</summary>
    public event Action? StateChanged;
    /// <summary>プロファイル一覧が変わった（UI スレッドで呼ばれる）。</summary>
    public event Action? ProfilesChanged;

    public IReadOnlyList<DeviceDefinition> Definitions => _definitions;

    public AppHost(FileLogger log, ProfileStore store, bool smokeTest)
    {
        Current = this;
        _ui = Application.Current.Dispatcher;
        Log = log;
        Store = store;
        SmokeTest = smokeTest;
        Settings = store.LoadSettings();
    }

    public void Start()
    {
        foreach (var p in Store.LoadProfiles()) Profiles.Add(p);
        ReloadDefinitions();
        foreach (var n in Store.Notices) StartupNotices.Add(FormatNotice(n));

        // スモークテスト（CI）では実際の入力を送らない
        IOutputSink sink = SmokeTest ? new RecordingSink() : new SendInputSink(Log);
        Engine = new PadEngine(sink, Log);
        Engine.Requested += r => _ui.BeginInvoke(() => OnEngineRequest(r));
        Engine.PausedChanged += _ => _ui.BeginInvoke(RaiseStateChanged);
        Engine.GyroEnabledChanged += _ => _ui.BeginInvoke(RaiseStateChanged);
        Engine.OverlayNavigated += nav => _ui.BeginInvoke(() => Overlay?.Navigate(nav));
        Engine.Start();

        Controllers = new ControllerManager(Log, () => _definitions, () => Settings);
        Controllers.StateReceived += Engine.PostState;
        Controllers.ConnectionChanged += c =>
        {
            if (!c.Connected) Engine.DeviceDisconnected();
            _ui.BeginInvoke(RaiseStateChanged);
        };
        Controllers.HotKey += id =>
        {
            if (id == PauseHotkeyId) Engine.TogglePause();
        };
        Controllers.Start();
        RegisterPauseHotkey();

        Foreground = new ForegroundWatcher(Log, () => Settings.LogWindowTitles);
        Foreground.Changed += OnForegroundChanged;
        Foreground.Start();
        if (ActiveProfileId == "") ApplyProfile(Settings.DefaultProfileId, manual: false, notify: false);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (!SmokeTest) Tray = new TrayIcon(this);
        Overlay = new OverlayWindow(this);
        RaiseStateChanged();
        Log.Info($"起動完了（管理者権限: {StartupTask.IsElevated}）");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Engine.ReleaseAll();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Log.Info("スリープから復帰：デバイスを再列挙します");
            Engine.ReleaseAll();
            Controllers.ScheduleRefresh(1500);
            _ui.BeginInvoke(RegisterPauseHotkey);
        }
    }

    // ---------- デバイス定義 ----------

    public void ReloadDefinitions()
    {
        var list = Store.LoadDeviceDefinitions();
        list.AddRange(BuiltInDevices.All());
        _definitions = list;
    }

    public void SaveDefinition(DeviceDefinition def)
    {
        Store.SaveDeviceDefinition(def);
        ReloadDefinitions();
        Controllers.Restart();
    }

    // ---------- プロファイル ----------

    public Profile? FindProfile(string? id) =>
        id == null ? null : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public string ProfileName(string id) =>
        id == DefaultProfiles.UiNavigationId ? Loc.T("Profile_UiNav") : FindProfile(id)?.Name ?? id;

    private void ApplyProfile(string id, bool manual, bool notify)
    {
        Profile? p = id == DefaultProfiles.UiNavigationId ? DefaultProfiles.UiNavigation() : FindProfile(id);
        p ??= FindProfile(Settings.DefaultProfileId) ?? Profiles.FirstOrDefault() ?? DefaultProfiles.Desktop();
        bool changed = p.Id != ActiveProfileId;
        ActiveProfileId = p.Id;
        IsManualOverride = manual;
        Engine.SetProfile(p.DeepClone());
        if (changed && notify && Settings.NotifyOnSwitch && p.Id != DefaultProfiles.UiNavigationId)
            Tray?.Notify("PadMapper", Loc.F("Notify_Switched", p.Name));
        RaiseStateChanged();
    }

    /// <summary>トレイ・オーバーレイ・バインディングからの手動切り替え。次に前面のアプリが変わるまで有効。</summary>
    public void SwitchProfileManually(string id)
    {
        if (id == "next")
        {
            var list = Profiles.ToList();
            int i = list.FindIndex(p => p.Id == ActiveProfileId);
            id = list.Count == 0 ? Settings.DefaultProfileId : list[(i + 1) % list.Count].Id;
        }
        IsUiNavigationActive = false;
        ApplyProfile(id, manual: true, notify: true);
    }

    private void OnForegroundChanged(ForegroundInfo fg)
    {
        if (Foreground.IsSelf(fg))
        {
            // 本ソフトの画面が前面：コントローラーで画面を操作できるようにする
            if (Settings.ControllerUiNavigation && !SmokeTest && !IsUiNavigationActive)
            {
                _profileBeforeUiNav = ActiveProfileId;
                IsUiNavigationActive = true;
                ApplyProfile(DefaultProfiles.UiNavigationId, manual: false, notify: false);
            }
            return;
        }
        bool wasUiNav = IsUiNavigationActive;
        IsUiNavigationActive = false;
        if (IsManualOverride && wasUiNav && _profileBeforeUiNav != null)
        {
            // 手動で選んだプロファイルのまま本ソフトの画面を見ていただけなら、元に戻す
            ApplyProfile(_profileBeforeUiNav, manual: true, notify: false);
            return;
        }
        ResolveFor(fg, notify: true);
    }

    public void ResolveFor(ForegroundInfo fg, bool notify)
    {
        var ids = Profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = ProfileResolver.Resolve(fg, Settings.Rules, Settings.DefaultProfileId, ids);
        ApplyProfile(id, manual: false, notify: notify);
    }

    /// <summary>プロファイルを保存する（使用中なら即座に反映）。</summary>
    public void SaveProfile(Profile p)
    {
        Store.SaveProfile(p);
        var existing = FindProfile(p.Id);
        if (existing == null) Profiles.Add(p);
        else Profiles[Profiles.IndexOf(existing)] = p;
        if (p.Id == ActiveProfileId) Engine.SetProfile(p.DeepClone());
        ProfilesChanged?.Invoke();
        RaiseStateChanged();
    }

    public void DeleteProfile(string id)
    {
        var p = FindProfile(id);
        if (p == null || Profiles.Count <= 1) return;
        Store.DeleteProfile(id);
        Profiles.Remove(p);
        if (Settings.DefaultProfileId == id)
        {
            Settings.DefaultProfileId = Profiles[0].Id;
            SaveSettings();
        }
        if (ActiveProfileId == id) ApplyProfile(Settings.DefaultProfileId, manual: false, notify: false);
        ProfilesChanged?.Invoke();
    }

    // ---------- 設定 ----------

    public void SaveSettings()
    {
        Store.SaveSettings(Settings);
        RegisterPauseHotkey();
        if (!IsManualOverride && !IsUiNavigationActive) ResolveFor(Foreground.LastExternal, notify: false);
        RaiseStateChanged();
    }

    /// <summary>直前に前面だった（本ソフト以外の）アプリを、指定のプロファイルに紐づける。</summary>
    public AppRule? BindLastApp(string profileId)
    {
        var fg = Foreground.LastExternal;
        if (string.IsNullOrEmpty(fg.ExeName)) return null;
        var rule = Settings.Rules.FirstOrDefault(r => string.Equals(r.Match.Exe, fg.ExeName, StringComparison.OrdinalIgnoreCase)
                                                      && string.IsNullOrEmpty(r.Match.TitleRegex) && string.IsNullOrEmpty(r.Match.Path));
        if (rule == null)
        {
            rule = new AppRule { Match = new AppMatch { Exe = fg.ExeName }, ProfileId = profileId };
            Settings.Rules.Insert(0, rule);
        }
        else rule.ProfileId = profileId;
        SaveSettings();
        return rule;
    }

    private void RegisterPauseHotkey()
    {
        var window = Controllers?.Window;
        if (window == null) return;
        var text = Settings.PauseHotkey;
        window.Invoke(() =>
        {
            Win32.UnregisterHotKey(window.Hwnd, PauseHotkeyId);
            HotkeyRegistered = false;
            if (!Hotkey.TryParse(text, out var hk) || !KeyCatalog.TryGet(hk.Key, out var key)) return;
            uint vk = key.Vk != 0 ? key.Vk : Win32.MapVirtualKey(key.Extended ? (uint)(0xE000 | key.Scan) : key.Scan, 3);
            uint mods = Win32.MOD_NOREPEAT | (hk.Ctrl ? Win32.MOD_CONTROL : 0) | (hk.Alt ? Win32.MOD_ALT : 0)
                        | (hk.Shift ? Win32.MOD_SHIFT : 0) | (hk.Win ? Win32.MOD_WIN : 0);
            HotkeyRegistered = vk != 0 && Win32.RegisterHotKey(window.Hwnd, PauseHotkeyId, mods, vk);
            if (!HotkeyRegistered) Log.Warn($"ホットキーを登録できません: {text}");
        });
    }

    // ---------- エンジンからの要求 ----------

    private void OnEngineRequest(EngineRequest r)
    {
        switch (r.Kind)
        {
            case EngineRequestKind.SwitchProfile:
                if (r.ProfileId != null) SwitchProfileManually(r.ProfileId);
                break;
            case EngineRequestKind.OpenOverlay:
                Overlay?.Toggle();
                break;
        }
    }

    // ---------- ウィンドウ ----------

    public void ShowMainWindow()
    {
        if (MainWindow == null) return;
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    public void RaiseStateChanged()
    {
        Tray?.Update(Engine?.IsPaused == true, ProfileName(ActiveProfileId));
        StateChanged?.Invoke();
    }

    private static string FormatNotice(LoadNotice n) => n.Kind switch
    {
        LoadNoticeKind.RestoredFromBackup => Loc.F("Notice_Restored", n.FileName, n.BackupName),
        _ => Loc.F("Notice_Failed", n.FileName),
    };

    /// <summary>終了：押されている出力を全て離してから止める。</summary>
    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Dispose();
        Application.Current.Shutdown(0);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { Engine?.Stop(); } catch (Exception ex) { Log.Error("停止時の例外", ex); }
        try { Controllers?.Dispose(); } catch { }
        try { Foreground?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        Log.Info("終了");
    }
}
