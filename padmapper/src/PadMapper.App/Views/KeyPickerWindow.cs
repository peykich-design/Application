using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PadMapper.App.Services;
using PadMapper.Core.Output;

namespace PadMapper.App.Views;

/// <summary>
/// キーの選択画面。画面上のキーボードをクリックして選ぶか、「よく使う操作」（ブラウザのタブ切り替えなど）から選ぶ。
/// 修飾キー（Ctrl / Shift / Alt / Win）は上のボタンで切り替える。
/// </summary>
public sealed class KeyPickerWindow : Window
{
    private const double Unit = 44;

    // キー名, 表示, 幅（キー1個分を 1 とする）。名前が空なら隙間。
    private static readonly (string key, string label, double width)[][] MainRows =
    {
        new[] { K("Esc", "Esc"), Gap(0.6), K("F1"), K("F2"), K("F3"), K("F4"), Gap(0.4), K("F5"), K("F6"), K("F7"), K("F8"), Gap(0.4), K("F9"), K("F10"), K("F11"), K("F12") },
        new[] { K("Grave", "半/全"), K("D1", "1"), K("D2", "2"), K("D3", "3"), K("D4", "4"), K("D5", "5"), K("D6", "6"), K("D7", "7"), K("D8", "8"), K("D9", "9"), K("D0", "0"), K("Minus", "-"), K("Equals", "^ ="), K("Backspace", "BS", 2) },
        new[] { K("Tab", "Tab", 1.5), K("Q"), K("W"), K("E"), K("R"), K("T"), K("Y"), K("U"), K("I"), K("O"), K("P"), K("LBracket", "@ ["), K("RBracket", "[ ]"), K("Backslash", "\\", 1.5) },
        new[] { K("CapsLock", "Caps", 1.75), K("A"), K("S"), K("D"), K("F"), K("G"), K("H"), K("J"), K("K"), K("L"), K("Semicolon", ";"), K("Quote", ": '"), K("Enter", "Enter", 2.25) },
        new[] { K("LShift", "Shift", 2.25), K("Z"), K("X"), K("C"), K("V"), K("B"), K("N"), K("M"), K("Comma", ","), K("Period", "."), K("Slash", "/"), K("RShift", "Shift(右)", 2.75) },
        new[] { K("LCtrl", "Ctrl", 1.25), K("LWin", "Win", 1.25), K("LAlt", "Alt", 1.25), K("Muhenkan", "無変換", 1.25), K("Space", "Space", 3.5), K("Henkan", "変換", 1.25), K("Kana", "かな", 1.25), K("RAlt", "Alt(右)", 1.25), K("Apps", "Menu", 1.25), K("RCtrl", "Ctrl(右)", 1.25) },
    };

    private static readonly (string key, string label, double width)[][] NavRows =
    {
        new[] { K("PrintScreen", "PrtSc"), K("ScrollLock", "ScrLk"), K("Pause", "Pause") },
        new[] { K("Insert", "Ins"), K("Home", "Home"), K("PageUp", "PgUp") },
        new[] { K("Delete", "Del"), K("End", "End"), K("PageDown", "PgDn") },
        Array.Empty<(string, string, double)>(),
        new[] { Gap(1), K("Up", "↑"), Gap(1) },
        new[] { K("Left", "←"), K("Down", "↓"), K("Right", "→") },
    };

    private static readonly (string key, string label, double width)[][] NumRows =
    {
        Array.Empty<(string, string, double)>(),
        new[] { K("NumLock", "Num"), K("NumDivide", "/"), K("NumMultiply", "*"), K("NumSubtract", "-") },
        new[] { K("Num7", "7"), K("Num8", "8"), K("Num9", "9"), K("NumAdd", "+") },
        new[] { K("Num4", "4"), K("Num5", "5"), K("Num6", "6"), Gap(1) },
        new[] { K("Num1", "1"), K("Num2", "2"), K("Num3", "3"), K("NumEnter", "Ent") },
        new[] { K("Num0", "0", 2), K("NumDecimal", "."), Gap(1) },
    };

    private static readonly (string key, string label, double width)[] ExtraKeys =
    {
        K("F13"), K("F14"), K("F15"), K("F16"), K("F17"), K("F18"), K("F19"), K("F20"), K("F21"), K("F22"), K("F23"), K("F24"),
    };

    private static readonly (string key, string label, double width)[] MediaKeys =
    {
        K("MediaPlayPause", "▶/❚❚", 1.5), K("MediaStop", "■"), K("MediaPrev", "⏮"), K("MediaNext", "⏭"),
        K("VolumeMute", "🔇"), K("VolumeDown", "🔉"), K("VolumeUp", "🔊"),
        K("BrowserBack", "戻る", 1.5), K("BrowserForward", "進む", 1.5),
    };

    private static (string, string, double) K(string key, string? label = null, double width = 1) => (key, label ?? key, width);
    private static (string, string, double) Gap(double width) => ("", "", width);

    private readonly Dictionary<string, Button> _keyButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToggleButton _ctrl = Mod("Ctrl"), _shift = Mod("Shift"), _alt = Mod("Alt"), _win = Mod("Win");
    private readonly TextBlock _preview = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public string? SelectedKey { get; private set; }

    public List<string> SelectedModifiers
    {
        get
        {
            var list = new List<string>();
            if (_ctrl.IsChecked == true) list.Add("LCtrl");
            if (_shift.IsChecked == true) list.Add("LShift");
            if (_alt.IsChecked == true) list.Add("LAlt");
            if (_win.IsChecked == true) list.Add("LWin");
            return list;
        }
    }

    public KeyPickerWindow(string? key, IEnumerable<string>? modifiers)
    {
        Title = Loc.T("Picker_Title");
        Width = 1080;
        Height = 620;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        SelectedKey = KeyCatalog.Normalize(key);
        var mods = (modifiers ?? Enumerable.Empty<string>()).Select(m => KeyCatalog.Normalize(m) ?? m).ToList();
        _ctrl.IsChecked = mods.Any(m => m.EndsWith("Ctrl"));
        _shift.IsChecked = mods.Any(m => m.EndsWith("Shift"));
        _alt.IsChecked = mods.Any(m => m.EndsWith("Alt"));
        _win.IsChecked = mods.Any(m => m.EndsWith("Win"));
        foreach (var t in new[] { _ctrl, _shift, _alt, _win }) t.Click += (_, _) => UpdatePreview();

        var root = new DockPanel { Margin = new Thickness(16) };

        // 上：修飾キーと選択中の表示
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Text(Loc.T("Picker_Modifiers"), 0, 0, 8, 0));
        foreach (var t in new[] { _ctrl, _shift, _alt, _win }) top.Children.Add(t);
        top.Children.Add(_preview);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // 下：OK / キャンセル
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Wpf.Ui.Controls.Button { Content = Loc.T("Common_Ok"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (SelectedKey == null) return;
            DialogResult = true;
        };
        var cancel = new Button { Content = Loc.T("Setup_Cancel"), MinWidth = 100, IsCancel = true };
        bottom.Children.Add(ok);
        bottom.Children.Add(cancel);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // 中：タブ（キーボード / よく使う操作）
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = Loc.T("Picker_TabPresets"), Content = BuildPresets() });
        tabs.Items.Add(new TabItem { Header = Loc.T("Picker_TabKeyboard"), Content = BuildKeyboard() });
        // 既に選ばれているキーがプリセットでなければ、キーボードのタブを開く
        tabs.SelectedIndex = SelectedKey != null && ShortcutPresets.Find(SelectedKey, mods) == null ? 1 : 0;
        root.Children.Add(tabs);

        Content = root;
        UpdatePreview();
    }

    private static ToggleButton Mod(string text) => new() { Content = text, MinWidth = 64, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6) };

    private static TextBlock Text(string text, double l = 0, double t = 0, double r = 0, double b = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(l, t, r, b), TextWrapping = TextWrapping.Wrap };

    // ---------- キーボード ----------

    private UIElement BuildKeyboard()
    {
        var blocks = new StackPanel { Orientation = Orientation.Horizontal };
        blocks.Children.Add(BuildBlock(MainRows));
        blocks.Children.Add(new Border { Width = Unit * 0.4 });
        blocks.Children.Add(BuildBlock(NavRows));
        blocks.Children.Add(new Border { Width = Unit * 0.4 });
        blocks.Children.Add(BuildBlock(NumRows));

        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new Viewbox { Child = blocks, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left });
        panel.Children.Add(Text(Loc.T("Picker_Extra"), 0, 12, 0, 4));
        panel.Children.Add(Row(ExtraKeys));
        panel.Children.Add(Text(Loc.T("Picker_Media"), 0, 12, 0, 4));
        panel.Children.Add(Row(MediaKeys));
        panel.Children.Add(Text(Loc.T("Picker_KeyboardHint"), 0, 12, 0, 0));
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private StackPanel BuildBlock((string key, string label, double width)[][] rows)
    {
        var block = new StackPanel();
        foreach (var row in rows)
        {
            var r = Row(row);
            r.Height = Unit + 4;
            block.Children.Add(r);
        }
        return block;
    }

    private StackPanel Row(IEnumerable<(string key, string label, double width)> keys)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, label, width) in keys)
        {
            if (key == "")
            {
                row.Children.Add(new Border { Width = Unit * width });
                continue;
            }
            var b = new Button
            {
                Content = new TextBlock { Text = label, FontSize = label.Length > 4 ? 11 : 13, TextAlignment = TextAlignment.Center },
                Width = Unit * width - 4,
                Height = Unit - 4,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                ToolTip = key,
            };
            b.Click += (_, _) => Select(key);
            row.Children.Add(b);
            _keyButtons[key] = b;
        }
        return row;
    }

    // ---------- よく使う操作 ----------

    private UIElement BuildPresets()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        foreach (var group in ShortcutPresets.All.GroupBy(p => p.Category))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("PresetGroup_" + group.Key), FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4),
            });
            var wrap = new WrapPanel();
            foreach (var p in group)
            {
                var b = new Button
                {
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = Loc.T(p.NameKey), FontWeight = FontWeights.SemiBold },
                            new TextBlock { Text = Describe(p.Key, p.Modifiers), FontSize = 11, Opacity = 0.7 },
                        },
                    },
                    MinWidth = 150,
                    Margin = new Thickness(0, 0, 6, 6),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                b.Click += (_, _) =>
                {
                    _ctrl.IsChecked = p.Modifiers.Contains("LCtrl");
                    _shift.IsChecked = p.Modifiers.Contains("LShift");
                    _alt.IsChecked = p.Modifiers.Contains("LAlt");
                    _win.IsChecked = p.Modifiers.Contains("LWin");
                    Select(p.Key);
                };
                wrap.Children.Add(b);
            }
            panel.Children.Add(wrap);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ---------- 選択 ----------

    private void Select(string key)
    {
        SelectedKey = KeyCatalog.Normalize(key) ?? key;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        foreach (var (key, b) in _keyButtons)
        {
            bool selected = string.Equals(key, SelectedKey, StringComparison.OrdinalIgnoreCase);
            if (selected)
            {
                b.SetResourceReference(BackgroundProperty, "AccentFillColorDefaultBrush");
                b.SetResourceReference(ForegroundProperty, "TextOnAccentFillColorPrimaryBrush");
            }
            else
            {
                b.ClearValue(BackgroundProperty);
                b.ClearValue(ForegroundProperty);
            }
        }
        if (SelectedKey == null)
        {
            _preview.Text = Loc.T("Picker_NoKey");
            return;
        }
        var text = Describe(SelectedKey, SelectedModifiers);
        var preset = ShortcutPresets.Find(SelectedKey, SelectedModifiers);
        _preview.Text = preset != null ? $"{text}（{Loc.T(preset.NameKey)}）" : text;
    }

    /// <summary>"Ctrl + Shift + T" のような表示。</summary>
    public static string Describe(string key, IEnumerable<string>? modifiers)
    {
        var parts = (modifiers ?? Enumerable.Empty<string>())
            .Select(m => (KeyCatalog.Normalize(m) ?? m) switch
            {
                "LCtrl" or "RCtrl" => "Ctrl",
                "LShift" or "RShift" => "Shift",
                "LAlt" or "RAlt" => "Alt",
                "LWin" or "RWin" => "Win",
                var other => other,
            })
            .ToList();
        parts.Add(key);
        return string.Join(" + ", parts);
    }
}
