using PadMapper.Core.Output;

namespace PadMapper.Core.Profiles;

public enum MacroStepKind { KeyDown, KeyUp, MouseDown, MouseUp, Wait, Scroll, Move }

public readonly record struct MacroStep(MacroStepKind Kind, string? Key = null, MouseButton Button = MouseButton.Left,
    int Ms = 0, int Dx = 0, int Dy = 0, double Amount = 0, bool Horizontal = false);

/// <summary>
/// マクロの文字列を手順に変換する。区切りは ; か改行。
/// <code>
/// Ctrl+C            … 修飾キー付きで1回押す（tap と同じ）
/// tap Enter         … 押して離す
/// down LShift / up LShift
/// wait 50           … ミリ秒待つ
/// click left / mdown right / mup right
/// scroll 1 / hscroll -1
/// move 10 -5
/// </code>
/// </summary>
public static class MacroParser
{
    /// <summary>tap の押下時間（ミリ秒）。押してすぐ離すとゲームが取りこぼすことがあるため少し待つ。</summary>
    public const int TapMs = 20;

    public static List<MacroStep> Parse(string? text, out List<string> errors)
    {
        errors = new List<string>();
        var steps = new List<MacroStep>();
        if (string.IsNullOrWhiteSpace(text)) return steps;

        foreach (var raw in text.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant();
            string Arg(int i) => parts.Length > i ? parts[i] : "";

            switch (cmd)
            {
                case "down":
                case "up":
                {
                    var key = KeyCatalog.Normalize(Arg(1));
                    if (key == null) { errors.Add($"不明なキー: {Arg(1)}"); break; }
                    steps.Add(new MacroStep(cmd == "down" ? MacroStepKind.KeyDown : MacroStepKind.KeyUp, key));
                    break;
                }
                case "tap":
                case "key":
                    AddCombo(steps, errors, Arg(1));
                    break;
                case "wait":
                case "delay":
                    if (int.TryParse(Arg(1), out var ms) && ms >= 0 && ms <= 60000) steps.Add(new MacroStep(MacroStepKind.Wait, Ms: ms));
                    else errors.Add($"待ち時間が不正: {Arg(1)}");
                    break;
                case "click":
                case "mdown":
                case "mup":
                {
                    if (!TryButton(Arg(1).Length == 0 ? "left" : Arg(1), out var b)) { errors.Add($"不明なマウスボタン: {Arg(1)}"); break; }
                    if (cmd != "mup") steps.Add(new MacroStep(MacroStepKind.MouseDown, Button: b));
                    if (cmd == "click") steps.Add(new MacroStep(MacroStepKind.Wait, Ms: TapMs));
                    if (cmd != "mdown") steps.Add(new MacroStep(MacroStepKind.MouseUp, Button: b));
                    break;
                }
                case "scroll":
                case "hscroll":
                    if (double.TryParse(Arg(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                        steps.Add(new MacroStep(MacroStepKind.Scroll, Amount: amount, Horizontal: cmd == "hscroll"));
                    else errors.Add($"スクロール量が不正: {Arg(1)}");
                    break;
                case "move":
                    if (int.TryParse(Arg(1), out var dx) && int.TryParse(Arg(2), out var dy))
                        steps.Add(new MacroStep(MacroStepKind.Move, Dx: dx, Dy: dy));
                    else errors.Add($"移動量が不正: {line}");
                    break;
                default:
                    // "Ctrl+C" や "Enter" だけ書いた場合
                    if (parts.Length == 1) AddCombo(steps, errors, parts[0]);
                    else errors.Add($"不明な命令: {line}");
                    break;
            }
        }
        return steps;
    }

    private static void AddCombo(List<MacroStep> steps, List<string> errors, string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => (raw: k, name: KeyCatalog.Normalize(k))).ToList();
        if (keys.Count == 0) { errors.Add("キーが空です"); return; }
        var bad = keys.Where(k => k.name == null).ToList();
        if (bad.Count > 0) { errors.Add($"不明なキー: {string.Join(", ", bad.Select(b => b.raw))}"); return; }
        foreach (var k in keys) steps.Add(new MacroStep(MacroStepKind.KeyDown, k.name));
        steps.Add(new MacroStep(MacroStepKind.Wait, Ms: TapMs));
        for (int i = keys.Count - 1; i >= 0; i--) steps.Add(new MacroStep(MacroStepKind.KeyUp, keys[i].name));
    }

    public static bool TryButton(string s, out MouseButton b) =>
        Enum.TryParse(s.Trim(), ignoreCase: true, out b) && Enum.IsDefined(b);
}
