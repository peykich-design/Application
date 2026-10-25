namespace PadMapper.Core.Output;

/// <summary>キーの情報。Scan が 0 のキーは仮想キーコード（Vk）で送る。</summary>
public readonly record struct KeyInfo(string Name, ushort Scan, bool Extended, ushort Vk);

/// <summary>
/// キー名 → スキャンコードの表。SendInput はゲーム互換性のためスキャンコードで送る（SPEC 5章）。
/// スキャンコードはセット1（PC/AT）。E0 付きのキーは Extended。
/// </summary>
public static class KeyCatalog
{
    private static readonly Dictionary<string, KeyInfo> Keys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Ordered = new();

    static KeyCatalog()
    {
        void K(string name, ushort scan, bool ext = false, ushort vk = 0)
        {
            Keys[name] = new KeyInfo(name, scan, ext, vk);
            Ordered.Add(name);
        }

        // 文字キー
        string letters = "QWERTYUIOP";
        for (int i = 0; i < letters.Length; i++) K(letters[i].ToString(), (ushort)(0x10 + i));
        letters = "ASDFGHJKL";
        for (int i = 0; i < letters.Length; i++) K(letters[i].ToString(), (ushort)(0x1E + i));
        letters = "ZXCVBNM";
        for (int i = 0; i < letters.Length; i++) K(letters[i].ToString(), (ushort)(0x2C + i));
        // 数字キー
        for (int i = 1; i <= 9; i++) K("D" + i, (ushort)(0x01 + i));
        K("D0", 0x0B);
        // ファンクションキー
        for (int i = 1; i <= 10; i++) K("F" + i, (ushort)(0x3A + i));
        K("F11", 0x57); K("F12", 0x58);
        for (int i = 13; i <= 23; i++) K("F" + i, (ushort)(0x64 + i - 13));
        K("F24", 0x76);

        K("Esc", 0x01); K("Tab", 0x0F); K("Enter", 0x1C); K("Space", 0x39); K("Backspace", 0x0E);
        K("CapsLock", 0x3A); K("ScrollLock", 0x46);
        K("NumLock", 0, vk: 0x90); K("Pause", 0, vk: 0x13);
        K("LShift", 0x2A); K("RShift", 0x36);
        K("LCtrl", 0x1D); K("RCtrl", 0x1D, true);
        K("LAlt", 0x38); K("RAlt", 0x38, true);
        K("LWin", 0x5B, true); K("RWin", 0x5C, true); K("Apps", 0x5D, true);
        K("Minus", 0x0C); K("Equals", 0x0D); K("LBracket", 0x1A); K("RBracket", 0x1B);
        K("Semicolon", 0x27); K("Quote", 0x28); K("Grave", 0x29); K("Backslash", 0x2B);
        K("Comma", 0x33); K("Period", 0x34); K("Slash", 0x35);
        // 矢印・編集キー（E0）
        K("Up", 0x48, true); K("Down", 0x50, true); K("Left", 0x4B, true); K("Right", 0x4D, true);
        K("Home", 0x47, true); K("End", 0x4F, true); K("PageUp", 0x49, true); K("PageDown", 0x51, true);
        K("Insert", 0x52, true); K("Delete", 0x53, true); K("PrintScreen", 0x37, true);
        // テンキー
        K("Num0", 0x52); K("Num1", 0x4F); K("Num2", 0x50); K("Num3", 0x51); K("Num4", 0x4B);
        K("Num5", 0x4C); K("Num6", 0x4D); K("Num7", 0x47); K("Num8", 0x48); K("Num9", 0x49);
        K("NumAdd", 0x4E); K("NumSubtract", 0x4A); K("NumMultiply", 0x37); K("NumDivide", 0x35, true);
        K("NumDecimal", 0x53); K("NumEnter", 0x1C, true);
        // メディア・ブラウザ（E0）
        K("VolumeMute", 0x20, true); K("VolumeDown", 0x2E, true); K("VolumeUp", 0x30, true);
        K("MediaPlayPause", 0x22, true); K("MediaStop", 0x24, true);
        K("MediaNext", 0x19, true); K("MediaPrev", 0x10, true);
        K("BrowserBack", 0x6A, true); K("BrowserForward", 0x69, true);
        // 日本語キーボード
        K("HankakuZenkaku", 0x29); K("Henkan", 0x79); K("Muhenkan", 0x7B); K("Kana", 0x70);

        void A(string alias, string target) => Aliases[alias] = target;
        A("Ctrl", "LCtrl"); A("Control", "LCtrl"); A("Shift", "LShift"); A("Alt", "LAlt"); A("Win", "LWin");
        A("Escape", "Esc"); A("Return", "Enter"); A("Del", "Delete"); A("Ins", "Insert");
        A("PgUp", "PageUp"); A("PgDn", "PageDown"); A("Back", "Backspace"); A("Menu", "Apps");
        for (int i = 0; i <= 9; i++) A(i.ToString(), "D" + i);
        A("-", "Minus"); A("=", "Equals"); A("[", "LBracket"); A("]", "RBracket"); A(";", "Semicolon");
        A("'", "Quote"); A("`", "Grave"); A("\\", "Backslash"); A(",", "Comma"); A(".", "Period"); A("/", "Slash");
    }

    /// <summary>画面のキー選択に出す一覧（定義順）。</summary>
    public static IReadOnlyList<string> Names => Ordered;

    public static bool TryGet(string? name, out KeyInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.Trim();
        if (Aliases.TryGetValue(n, out var target)) n = target;
        return Keys.TryGetValue(n, out info);
    }

    /// <summary>エイリアスを正式名にする（不明なら null）。</summary>
    public static string? Normalize(string? name) => TryGet(name, out var info) ? info.Name : null;
}
