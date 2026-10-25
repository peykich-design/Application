namespace PadMapper.Core.Profiles;

/// <summary>%APPDATA%\PadMapper\settings.json に保存する全体設定。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    /// <summary>"ja" または "en"。</summary>
    public string Language { get; set; } = "ja";
    public string DefaultProfileId { get; set; } = "desktop";
    /// <summary>アプリ→プロファイルの紐づけ。上から順に評価し、最初に一致したものを使う。</summary>
    public List<AppRule> Rules { get; set; } = new();
    /// <summary>プロファイル自動切り替え時にトースト（バルーン）通知を出す。</summary>
    public bool NotifyOnSwitch { get; set; } = true;
    /// <summary>ログにウィンドウタイトルを残す（個人情報になりうるので既定はオフ）。</summary>
    public bool LogWindowTitles { get; set; }
    /// <summary>本ソフトのウィンドウが前面のとき、十字キー/A/B で画面を操作できるようにする。</summary>
    public bool ControllerUiNavigation { get; set; } = true;
    /// <summary>"rawInput"（既定）または "hidRead"（HID デバイスを直接読む）。"auto" は Raw Input で受信できなければ直接読みに切り替える。</summary>
    public string InputMethod { get; set; } = "auto";
    /// <summary>デバイス定義にある初期化コマンドを送る（実機で必要と分かった場合のみオン）。</summary>
    public bool SendInitCommand { get; set; }
    /// <summary>一時停止（緊急停止）のホットキー。例 "Ctrl+Alt+Shift+F12"。</summary>
    public string PauseHotkey { get; set; } = "Ctrl+Alt+Shift+F12";
    /// <summary>"system" / "light" / "dark"。</summary>
    public string Theme { get; set; } = "system";
}

public sealed class AppRule
{
    public AppMatch Match { get; set; } = new();
    public string ProfileId { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class AppMatch
{
    /// <summary>実行ファイル名（例 game.exe）。大文字小文字は区別しない。</summary>
    public string? Exe { get; set; }
    /// <summary>ウィンドウタイトルの正規表現。</summary>
    public string? TitleRegex { get; set; }
    /// <summary>実行ファイルのフルパス。* でワイルドカード。</summary>
    public string? Path { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Exe) && string.IsNullOrWhiteSpace(TitleRegex) && string.IsNullOrWhiteSpace(Path);
}
