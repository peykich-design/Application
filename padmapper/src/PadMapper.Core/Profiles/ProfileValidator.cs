using System.Text.RegularExpressions;
using PadMapper.Core.Model;
using PadMapper.Core.Output;

namespace PadMapper.Core.Profiles;

public enum IssueSeverity { Info, Warning, Error }

/// <summary>検証結果。Code は画面の文言リソースのキー（"V_" + Code）にも使う。</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Code, string Message, int BindingIndex = -1);

/// <summary>
/// プロファイルの検証。ゲーム型（パススルー）でできないこと（feature-list.md）を明示する。
/// </summary>
public static class ProfileValidator
{
    public static List<ValidationIssue> Validate(Profile p, IEnumerable<string>? profileIds = null)
    {
        var issues = new List<ValidationIssue>();
        var ids = profileIds?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shiftLayers = p.Bindings.Where(b => b.Trigger == TriggerKind.ModeShift && !string.IsNullOrEmpty(b.Layer))
            .Select(b => b.Layer!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(p.Id)) issues.Add(new(IssueSeverity.Error, "EmptyId", "プロファイル ID が空です"));

        for (int i = 0; i < p.Bindings.Count; i++)
        {
            var b = p.Bindings[i];
            if (!LogicalInputs.TryParse(b.Input, out var input))
            {
                issues.Add(new(IssueSeverity.Error, "UnknownInput", $"不明な入力: {b.Input}", i));
                continue;
            }

            if (b.Trigger == TriggerKind.ModeShift)
            {
                if (p.Type == ProfileType.Game)
                    issues.Add(new(IssueSeverity.Error, "ModeShiftInGame", "ゲーム型ではモードシフトは使えません（ゲームには元の入力が届くため）", i));
                if (string.IsNullOrWhiteSpace(b.Layer))
                    issues.Add(new(IssueSeverity.Error, "ModeShiftNoLayer", "モードシフトに切り替え先のレイヤー名がありません", i));
                continue;
            }

            if (b.Trigger == TriggerKind.Chord)
            {
                if (b.Chord == null || b.Chord.Count == 0)
                    issues.Add(new(IssueSeverity.Error, "ChordEmpty", "同時押しの相手の入力がありません", i));
                else
                    foreach (var c in b.Chord)
                        if (!LogicalInputs.TryParse(c, out _))
                            issues.Add(new(IssueSeverity.Error, "UnknownInput", $"不明な入力: {c}", i));
            }

            if (b.Trigger is TriggerKind.Hold or TriggerKind.Tap && b.EffectiveHoldMs is < 50 or > 10000)
                issues.Add(new(IssueSeverity.Warning, "HoldMsRange", "長押し時間は 50〜10000 ミリ秒にしてください", i));
            if (b.Trigger == TriggerKind.Double && b.EffectiveDoubleMs is < 50 or > 2000)
                issues.Add(new(IssueSeverity.Warning, "DoubleMsRange", "ダブルタップの間隔は 50〜2000 ミリ秒にしてください", i));

            if (!string.IsNullOrEmpty(b.Layer))
            {
                if (p.Type == ProfileType.Game)
                    issues.Add(new(IssueSeverity.Error, "LayerInGame", "ゲーム型ではレイヤーは使えません", i));
                else if (!shiftLayers.Contains(b.Layer))
                    issues.Add(new(IssueSeverity.Warning, "LayerWithoutShift", $"レイヤー「{b.Layer}」に切り替えるモードシフトがありません", i));
            }

            ValidateAction(b.Action, i, ids, issues);

            if (p.Type == ProfileType.Game && b.Action.Type != ActionType.None && !LogicalInputs.IsExtraButton(input))
                issues.Add(new(IssueSeverity.Warning, "DoubleInputInGame",
                    $"{b.Input} の元の入力もゲームに届きます（二重入力）。ゲームで使っていないボタンに割り当ててください", i));
        }

        if (p.Type == ProfileType.Game)
        {
            if (p.Sticks.Left.Mode != StickMode.None || p.Sticks.Right.Mode != StickMode.None)
                issues.Add(new(IssueSeverity.Error, "StickInGame", "ゲーム型ではスティックのマウス化・スクロール化は使えません（ゲームにもスティックの値が届くため）"));
        }

        var g = p.Gyro;
        if (g.Activation.Mode != GyroActivationMode.Always && !LogicalInputs.TryParse(g.Activation.Input, out _))
            issues.Add(new(IssueSeverity.Error, "GyroInputUnknown", "ジャイロを有効にするボタンが不明です"));
        if (!string.Equals(g.Output, "mouse", StringComparison.OrdinalIgnoreCase))
            issues.Add(new(IssueSeverity.Error, "GyroOutputUnsupported", "ジャイロの出力はマウスのみ対応です（スティック出力は仮想パッドが必要）"));

        var o = p.Overlay;
        if (o.Trigger != null && !LogicalInputs.TryParse(o.Trigger.Input, out _))
            issues.Add(new(IssueSeverity.Error, "OverlayInputUnknown", "オーバーレイを開くボタンが不明です"));
        foreach (var nav in new[] { o.Up, o.Down, o.Select, o.Close })
            if (!LogicalInputs.TryParse(nav, out _))
                issues.Add(new(IssueSeverity.Error, "OverlayInputUnknown", $"オーバーレイ操作のボタンが不明です: {nav}"));

        return issues;
    }

    private static void ValidateAction(ActionDef a, int index, HashSet<string>? ids, List<ValidationIssue> issues)
    {
        switch (a.Type)
        {
            case ActionType.Key:
                if (!KeyCatalog.TryGet(a.Key, out _))
                    issues.Add(new(IssueSeverity.Error, "UnknownKey", $"不明なキー: {a.Key}", index));
                foreach (var m in a.Modifiers ?? new List<string>())
                    if (!KeyCatalog.TryGet(m, out _))
                        issues.Add(new(IssueSeverity.Error, "UnknownKey", $"不明なキー: {m}", index));
                break;
            case ActionType.MouseButton:
                if (a.Button == null) issues.Add(new(IssueSeverity.Error, "NoMouseButton", "マウスボタンが指定されていません", index));
                break;
            case ActionType.Scroll:
                if (a.Amount is null or 0) issues.Add(new(IssueSeverity.Warning, "ScrollZero", "スクロール量が 0 です", index));
                break;
            case ActionType.Macro:
                MacroParser.Parse(a.Macro, out var errors);
                foreach (var e in errors) issues.Add(new(IssueSeverity.Error, "MacroInvalid", $"マクロ: {e}", index));
                break;
            case ActionType.Profile:
                if (string.IsNullOrWhiteSpace(a.Id))
                    issues.Add(new(IssueSeverity.Error, "ProfileMissing", "切り替え先のプロファイルがありません", index));
                else if (ids != null && a.Id != "next" && !ids.Contains(a.Id))
                    issues.Add(new(IssueSeverity.Error, "ProfileMissing", $"プロファイル「{a.Id}」がありません", index));
                break;
        }
    }

    /// <summary>アプリ紐づけルールの検証（正規表現の誤りなど）。</summary>
    public static List<string> ValidateRule(AppRule rule)
    {
        var errors = new List<string>();
        if (rule.Match.IsEmpty) errors.Add("条件（exe・タイトル・パス）が空です");
        if (!string.IsNullOrEmpty(rule.Match.TitleRegex))
        {
            try { _ = new Regex(rule.Match.TitleRegex); }
            catch (ArgumentException ex) { errors.Add("タイトルの正規表現が不正です: " + ex.Message); }
        }
        if (string.IsNullOrWhiteSpace(rule.ProfileId)) errors.Add("プロファイルが選ばれていません");
        return errors;
    }
}
