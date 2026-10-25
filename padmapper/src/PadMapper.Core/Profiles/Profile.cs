using System.Text.Json.Serialization;

namespace PadMapper.Core.Profiles;

/// <summary>
/// Desktop：フル機能（スティック→マウス、モードシフトなど）。
/// Game：パススルー。パッド入力はそのままゲームに届き、本ソフトは追加の出力だけを行う。
/// </summary>
public enum ProfileType { Desktop, Game }

public enum TriggerKind
{
    /// <summary>押している間だけ有効（キーなら押しっぱなし）。</summary>
    Press,
    /// <summary>離したときに1回。</summary>
    Release,
    /// <summary>短押し：holdMs 未満で離したときに1回。</summary>
    Tap,
    /// <summary>長押し：holdMs 以上押したら有効。</summary>
    Hold,
    /// <summary>ダブルタップ：doubleMs 以内に2回押したら有効。</summary>
    Double,
    /// <summary>同時押し：input と chord のすべてが押されている間有効。</summary>
    Chord,
    /// <summary>モードシフト：押している間、layer のバインディングに切り替える（Desktop のみ）。</summary>
    ModeShift,
}

public enum ActionType
{
    None,
    Key,
    MouseButton,
    Scroll,
    MouseMove,
    Macro,
    Profile,
    Pause,
    GyroToggle,
    Overlay,
}

public enum MouseButton { Left, Right, Middle, X1, X2 }

public sealed class Profile
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter<ProfileType>))]
    public ProfileType Type { get; set; } = ProfileType.Desktop;
    public List<Binding> Bindings { get; set; } = new();
    public SticksSettings Sticks { get; set; } = new();
    public TriggersSettings Triggers { get; set; } = new();
    public GyroSettings Gyro { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    public Profile DeepClone() => ProfileJson.Clone(this);
}

public sealed class Binding
{
    public string Input { get; set; } = "";
    public TriggerKind Trigger { get; set; } = TriggerKind.Press;
    public int? HoldMs { get; set; }
    public int? DoubleMs { get; set; }
    /// <summary>同時押し（Chord）で一緒に押す入力。</summary>
    public List<string>? Chord { get; set; }
    /// <summary>このバインディングが属するレイヤー（null は基本レイヤー）。ModeShift では切り替え先のレイヤー名。</summary>
    public string? Layer { get; set; }
    public ActionDef Action { get; set; } = new();

    public const int DefaultHoldMs = 500;
    public const int DefaultDoubleMs = 300;
    public int EffectiveHoldMs => HoldMs ?? DefaultHoldMs;
    public int EffectiveDoubleMs => DoubleMs ?? DefaultDoubleMs;
}

/// <summary>割り当て先。type によって使う項目が変わる（使わない項目は JSON に出さない）。</summary>
public sealed class ActionDef
{
    public ActionType Type { get; set; } = ActionType.None;
    /// <summary>key：キー名（KeyCatalog の名前）。</summary>
    public string? Key { get; set; }
    /// <summary>key：一緒に押す修飾キー（Ctrl / Shift / Alt / Win など）。</summary>
    public List<string>? Modifiers { get; set; }
    /// <summary>mouseButton：left / right / middle / x1 / x2。</summary>
    public MouseButton? Button { get; set; }
    /// <summary>scroll：ノッチ数（正で上・右）。</summary>
    public double? Amount { get; set; }
    public bool? Horizontal { get; set; }
    /// <summary>mouseMove：移動量（ピクセル）。</summary>
    public int? Dx { get; set; }
    public int? Dy { get; set; }
    /// <summary>macro：手順（例 "down LCtrl; tap C; up LCtrl; wait 50"）。MacroParser を参照。</summary>
    public string? Macro { get; set; }
    /// <summary>profile：切り替え先のプロファイル ID（"next" で次のプロファイル）。</summary>
    public string? Id { get; set; }

    public static ActionDef KeyPress(string key, params string[] modifiers) =>
        new() { Type = ActionType.Key, Key = key, Modifiers = modifiers.Length > 0 ? modifiers.ToList() : null };
    public static ActionDef Mouse(MouseButton b) => new() { Type = ActionType.MouseButton, Button = b };
}

public enum StickMode { None, Mouse, Scroll }

public sealed class SticksSettings
{
    public StickSettings Left { get; set; } = new();
    public StickSettings Right { get; set; } = new();
    /// <summary>LSUp などの仮想入力（十字キー化）が押されたとみなす倒し量。</summary>
    public double DirectionThreshold { get; set; } = 0.5;
}

public sealed class StickSettings
{
    public StickMode Mode { get; set; } = StickMode.None;
    public double Deadzone { get; set; } = 0.12;
    public double OuterDeadzone { get; set; } = 0.02;
    /// <summary>レスポンスカーブの指数（1 で直線、大きいほど中心付近が細かい）。</summary>
    public double Curve { get; set; } = 2.0;
    /// <summary>Mouse：最大まで倒したときの速度（ピクセル/秒）。Scroll：最大まで倒したときのノッチ/秒。</summary>
    public double Speed { get; set; } = 1600;
    /// <summary>Scroll：少しだけ倒したときの速さ（ノッチ/秒）。倒し量に応じて Speed まで上がる。</summary>
    public double MinSpeed { get; set; } = 1.0;
    /// <summary>Scroll：なめらかスクロール（1ノッチより細かい量で送る）。オフにすると1ノッチ単位。</summary>
    public bool SmoothScroll { get; set; } = true;
    public bool InvertY { get; set; }
}

public sealed class TriggersSettings
{
    /// <summary>LT/RT を押したとみなすアナログ値。</summary>
    public double Threshold { get; set; } = 0.3;
    /// <summary>2段階目（LT2/RT2）のアナログ値。</summary>
    public double SecondStage { get; set; } = 0.95;
}

public enum GyroActivationMode { Always, WhileHeld, Toggle }
public enum GyroAxis { Yaw, Roll, Player }

public sealed class GyroSettings
{
    public bool Enabled { get; set; }
    /// <summary>出力先。現在は "mouse" のみ（スティック出力は仮想パッドが必要なため対象外）。</summary>
    public string Output { get; set; } = "mouse";
    public double SensitivityX { get; set; } = 1.0;
    public double SensitivityY { get; set; } = 1.0;
    public double DeadzoneDps { get; set; } = 0.5;
    /// <summary>0（なし）〜1（強い）。</summary>
    public double Smoothing { get; set; } = 0.3;
    public GyroAccel Accel { get; set; } = new();
    public GyroActivation Activation { get; set; } = new();
    /// <summary>横方向に使う軸。Player は加速度センサーがある場合のみ有効（ないときは Yaw と同じ）。</summary>
    public GyroAxis Axis { get; set; } = GyroAxis.Yaw;
    public bool InvertX { get; set; }
    public bool InvertY { get; set; }
    /// <summary>静止時の自動キャリブレーション。</summary>
    public bool AutoCalibrate { get; set; } = true;
}

public sealed class GyroAccel
{
    public bool Enabled { get; set; }
    /// <summary>この速度（deg/s）以下は倍率 1。</summary>
    public double LowDps { get; set; } = 20;
    /// <summary>この速度以上で倍率が最大。</summary>
    public double HighDps { get; set; } = 150;
    public double MaxMultiplier { get; set; } = 2.0;
}

public sealed class GyroActivation
{
    public GyroActivationMode Mode { get; set; } = GyroActivationMode.Always;
    public string? Input { get; set; }
}

public sealed class OverlaySettings
{
    /// <summary>オーバーレイを開く入力。既定は背面ボタン M4 の長押し。</summary>
    public Binding? Trigger { get; set; } = new() { Input = "M4", Trigger = TriggerKind.Hold, HoldMs = 600 };
    /// <summary>メニュー操作用の入力（ゲームにも届くので、ゲームで使わないボタンを既定にしている）。</summary>
    public string Up { get; set; } = "M1";
    public string Down { get; set; } = "M2";
    public string Select { get; set; } = "M3";
    public string Close { get; set; } = "M4";
}
