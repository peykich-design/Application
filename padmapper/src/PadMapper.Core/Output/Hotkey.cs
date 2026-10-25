namespace PadMapper.Core.Output;

/// <summary>キーボードのホットキー（例 "Ctrl+Alt+Shift+F12"）。</summary>
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
{
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = new Hotkey(false, false, false, false, "");
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win": case "windows": win = true; break;
                default:
                    if (key != null) return false;
                    key = KeyCatalog.Normalize(raw);
                    if (key == null) return false;
                    break;
            }
        }
        if (key == null) return false;
        // 修飾キーなしのホットキーは誤操作が多いので認めない
        if (!ctrl && !alt && !shift && !win) return false;
        hotkey = new Hotkey(ctrl, alt, shift, win, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}
