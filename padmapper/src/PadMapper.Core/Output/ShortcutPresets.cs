namespace PadMapper.Core.Output;

/// <summary>よく使う操作（ブラウザ・Windows・編集・メディア）を、キーと修飾キーの組み合わせとして用意したもの。</summary>
public sealed record ShortcutPreset(string Id, string Category, string Key, string[] Modifiers)
{
    /// <summary>画面の文言キー（"Preset_" + Id）。</summary>
    public string NameKey => "Preset_" + Id;

    public bool Matches(string? key, IEnumerable<string>? modifiers)
    {
        if (!string.Equals(KeyCatalog.Normalize(key), KeyCatalog.Normalize(Key), StringComparison.OrdinalIgnoreCase)) return false;
        var a = (modifiers ?? Enumerable.Empty<string>()).Select(m => KeyCatalog.Normalize(m) ?? m).OrderBy(x => x).ToList();
        var b = Modifiers.Select(m => KeyCatalog.Normalize(m) ?? m).OrderBy(x => x).ToList();
        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }
}

public static class ShortcutPresets
{
    public const string Browser = "browser", Window = "window", Edit = "edit", Media = "media";

    private static ShortcutPreset P(string id, string cat, string key, params string[] mods) => new(id, cat, key, mods);

    public static readonly IReadOnlyList<ShortcutPreset> All = new[]
    {
        // ブラウザ（Chrome / Edge / Firefox 共通のショートカット）
        P("NextTab", Browser, "Tab", "LCtrl"),
        P("PrevTab", Browser, "Tab", "LCtrl", "LShift"),
        P("NewTab", Browser, "T", "LCtrl"),
        P("CloseTab", Browser, "W", "LCtrl"),
        P("ReopenTab", Browser, "T", "LCtrl", "LShift"),
        P("Back", Browser, "BrowserBack"),
        P("Forward", Browser, "BrowserForward"),
        P("Reload", Browser, "F5"),
        P("AddressBar", Browser, "L", "LCtrl"),
        P("Find", Browser, "F", "LCtrl"),
        P("ZoomIn", Browser, "Equals", "LCtrl"),
        P("ZoomOut", Browser, "Minus", "LCtrl"),
        P("ZoomReset", Browser, "D0", "LCtrl"),
        P("FullScreen", Browser, "F11"),
        P("PageDown", Browser, "PageDown"),
        P("PageUp", Browser, "PageUp"),
        P("Top", Browser, "Home"),
        P("Bottom", Browser, "End"),
        // Windows
        P("SwitchApp", Window, "Tab", "LAlt"),
        P("TaskView", Window, "Tab", "LWin"),
        P("StartMenu", Window, "LWin"),
        P("ShowDesktop", Window, "D", "LWin"),
        P("CloseWindow", Window, "F4", "LAlt"),
        P("Minimize", Window, "Down", "LWin"),
        P("Maximize", Window, "Up", "LWin"),
        P("Snip", Window, "S", "LWin", "LShift"),
        P("OnScreenKeyboard", Window, "O", "LWin", "LCtrl"),
        P("Explorer", Window, "E", "LWin"),
        // 編集
        P("Copy", Edit, "C", "LCtrl"),
        P("Paste", Edit, "V", "LCtrl"),
        P("Cut", Edit, "X", "LCtrl"),
        P("Undo", Edit, "Z", "LCtrl"),
        P("Redo", Edit, "Y", "LCtrl"),
        P("SelectAll", Edit, "A", "LCtrl"),
        P("Enter", Edit, "Enter"),
        P("Escape", Edit, "Esc"),
        // メディア
        P("PlayPause", Media, "MediaPlayPause"),
        P("NextTrack", Media, "MediaNext"),
        P("PrevTrack", Media, "MediaPrev"),
        P("VolumeUp", Media, "VolumeUp"),
        P("VolumeDown", Media, "VolumeDown"),
        P("Mute", Media, "VolumeMute"),
    };

    public static ShortcutPreset? Find(string? key, IEnumerable<string>? modifiers) =>
        All.FirstOrDefault(p => p.Matches(key, modifiers));
}
