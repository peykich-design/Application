namespace PadMapper.Core.Profiles;

/// <summary>初回起動時に作るプロファイルと、本ソフトの画面を操作するための組み込みプロファイル。</summary>
public static class DefaultProfiles
{
    public const string DesktopId = "desktop";
    public const string GameId = "game-default";
    /// <summary>本ソフトのウィンドウが前面のときに使う（保存しない）。</summary>
    public const string UiNavigationId = "builtin-ui-nav";

    public static List<Profile> Create() => new() { Desktop(), Game() };

    public static Profile Desktop() => new()
    {
        Id = DesktopId,
        Name = "デスクトップ",
        Type = ProfileType.Desktop,
        Sticks = new SticksSettings
        {
            Left = new StickSettings { Mode = StickMode.Mouse, Speed = 1600, Curve = 2.0, Deadzone = 0.12 },
            Right = new StickSettings { Mode = StickMode.Scroll, Speed = 12, Curve = 1.5, Deadzone = 0.15 },
        },
        Bindings =
        {
            new Binding { Input = "RT", Action = ActionDef.Mouse(MouseButton.Left) },
            new Binding { Input = "LT", Action = ActionDef.Mouse(MouseButton.Right) },
            new Binding { Input = "R3", Action = ActionDef.Mouse(MouseButton.Middle) },
            new Binding { Input = "A", Action = ActionDef.KeyPress("Enter") },
            new Binding { Input = "B", Action = ActionDef.KeyPress("Esc") },
            new Binding { Input = "X", Action = ActionDef.KeyPress("Backspace") },
            new Binding { Input = "Y", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "DPadUp", Action = ActionDef.KeyPress("Up") },
            new Binding { Input = "DPadDown", Action = ActionDef.KeyPress("Down") },
            new Binding { Input = "DPadLeft", Action = ActionDef.KeyPress("Left") },
            new Binding { Input = "DPadRight", Action = ActionDef.KeyPress("Right") },
            new Binding { Input = "LB", Action = ActionDef.KeyPress("BrowserBack") },
            new Binding { Input = "RB", Action = ActionDef.KeyPress("BrowserForward") },
            new Binding { Input = "Start", Action = ActionDef.KeyPress("LWin") },
            new Binding { Input = "Back", Action = ActionDef.KeyPress("Tab", "LWin") },
        },
        Gyro = new GyroSettings { Enabled = false },
    };

    public static Profile Game() => new()
    {
        Id = GameId,
        Name = "ゲーム共通",
        Type = ProfileType.Game,
        Bindings =
        {
            new Binding { Input = "M1", Trigger = TriggerKind.Press, Action = ActionDef.KeyPress("F13") },
            new Binding { Input = "M2", Trigger = TriggerKind.Hold, HoldMs = 500, Action = new ActionDef { Type = ActionType.Profile, Id = DesktopId } },
            new Binding { Input = "C", Trigger = TriggerKind.Double, Action = ActionDef.Mouse(MouseButton.Middle) },
        },
        Gyro = new GyroSettings
        {
            Enabled = true,
            SensitivityX = 1.0,
            SensitivityY = 1.0,
            DeadzoneDps = 0.5,
            Smoothing = 0.3,
            Activation = new GyroActivation { Mode = GyroActivationMode.WhileHeld, Input = "LT" },
        },
    };

    /// <summary>本ソフトの画面を十字キー・スティック・A/B で操作するためのプロファイル。</summary>
    public static Profile UiNavigation() => new()
    {
        Id = UiNavigationId,
        Name = "PadMapper 画面操作",
        Type = ProfileType.Desktop,
        Bindings =
        {
            new Binding { Input = "DPadUp", Action = ActionDef.KeyPress("Up") },
            new Binding { Input = "DPadDown", Action = ActionDef.KeyPress("Down") },
            new Binding { Input = "DPadLeft", Action = ActionDef.KeyPress("Left") },
            new Binding { Input = "DPadRight", Action = ActionDef.KeyPress("Right") },
            new Binding { Input = "LSUp", Action = ActionDef.KeyPress("Up") },
            new Binding { Input = "LSDown", Action = ActionDef.KeyPress("Down") },
            new Binding { Input = "LSLeft", Action = ActionDef.KeyPress("Left") },
            new Binding { Input = "LSRight", Action = ActionDef.KeyPress("Right") },
            new Binding { Input = "A", Action = ActionDef.KeyPress("Space") },
            new Binding { Input = "B", Action = ActionDef.KeyPress("Esc") },
            new Binding { Input = "RB", Action = ActionDef.KeyPress("Tab") },
            new Binding { Input = "LB", Action = ActionDef.KeyPress("Tab", "LShift") },
            new Binding { Input = "Start", Action = ActionDef.KeyPress("Enter") },
        },
        Overlay = new OverlaySettings(),
    };
}
