using System.Text;
using PadMapper.Core.Devices;

namespace PadMapper.Core.Diagnostics;

/// <summary>デバイス定義を docs/vader4pro-report.md と同じ形式の Markdown にする。</summary>
public static class ReportMarkdown
{
    /// <summary>ウィザードで特定すべき項目（Phase 0 の完了条件）。</summary>
    public static readonly string[] RequiredButtons =
    {
        "A", "B", "X", "Y", "LB", "RB", "L3", "R3", "Back", "Start", "Guide",
        "DPadUp", "DPadDown", "DPadLeft", "DPadRight", "M1", "M2", "M3", "M4", "C", "Z",
    };
    public static readonly string[] RequiredAxes = { "LX", "LY", "RX", "RY", "LT", "RT" };
    public static readonly string[] RequiredGyro = { "pitch", "yaw", "roll" };

    public static string Build(DeviceDefinition def, HidDeviceInfo? device, string? sampleHex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {def.Name} レポート解析結果");
        sb.AppendLine();
        sb.AppendLine($"- 出所：{(def.Source == "learned" ? "診断ウィザードで実機から取得" : "SDL のソース由来（実機未検証）")}");
        sb.AppendLine($"- 作成日時：{DateTime.Now:yyyy-MM-dd HH:mm}");
        if (device != null)
        {
            sb.AppendLine($"- デバイス：{device.Product}（VID/PID {device.VidPid}、Usage {device.UsageText}、{device.Connection}、入力レポート長 {device.InputReportLength}）");
        }
        if (def.ReportId is int rid) sb.AppendLine($"- レポート ID：0x{rid:X2}");
        foreach (var s in def.Signature) sb.AppendLine($"- 判定用の固定値：[{s.Offset}] = 0x{s.Value:X2}");
        if (!string.IsNullOrEmpty(sampleHex)) sb.AppendLine($"- 静止時のサンプル：`{sampleHex}`");
        sb.AppendLine();

        sb.AppendLine("## ボタン");
        sb.AppendLine("| ボタン | バイト位置 | マスク | 判定 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var name in RequiredButtons.Concat(def.Buttons.Select(b => b.Input)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var b = def.Buttons.FirstOrDefault(x => x.Input.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (b == null) sb.AppendLine($"| {name} | **未特定** | | |");
            else sb.AppendLine($"| {name} | {b.Offset} | 0x{b.Mask:X2} | {(b.Value is int v ? $"値 == 0x{v:X2}" : "ビットが 1")} |");
        }
        sb.AppendLine();

        sb.AppendLine("## スティック・トリガー");
        sb.AppendLine("| 軸 | バイト位置 | 形式 | 中心 | 最小 | 最大 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var name in RequiredAxes)
        {
            var a = def.Axes.FirstOrDefault(x => x.Axis.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (a == null) sb.AppendLine($"| {name} | **未特定** | | | | |");
            else sb.AppendLine($"| {name} | {a.Offset}{(a.OffsetHi is int hi ? $",{hi}" : "")} | {a.Format} | {a.Center?.ToString() ?? "-"} | {a.Min} | {a.Max} |");
        }
        sb.AppendLine();

        sb.AppendLine("## ジャイロ（deg/s）");
        sb.AppendLine("| 軸 | 下位バイト | 上位バイト | 係数 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var name in RequiredGyro)
        {
            var g = def.Gyro.FirstOrDefault(x => x.Axis.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (g == null) sb.AppendLine($"| {name} | **未特定** | | |");
            else sb.AppendLine($"| {name} | {g.Offset} | {g.OffsetHi ?? g.Offset + 1} | {g.Scale:G4} |");
        }
        sb.AppendLine();

        if (def.Accel.Count > 0)
        {
            sb.AppendLine("## 加速度（G）");
            sb.AppendLine("| 軸 | 下位バイト | 上位バイト | 係数 |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var a in def.Accel)
                sb.AppendLine($"| {a.Axis} | {a.Offset} | {a.OffsetHi ?? a.Offset + 1} | {a.Scale:G4} |");
            sb.AppendLine();
        }

        var missing = Missing(def).ToList();
        sb.AppendLine("## 特定できなかった項目");
        sb.AppendLine(missing.Count == 0 ? "なし" : string.Join("、", missing));
        return sb.ToString();
    }

    public static IEnumerable<string> Missing(DeviceDefinition def)
    {
        foreach (var n in RequiredButtons)
            if (!def.Buttons.Any(b => b.Input.Equals(n, StringComparison.OrdinalIgnoreCase))) yield return n;
        foreach (var n in RequiredAxes)
            if (!def.Axes.Any(a => a.Axis.Equals(n, StringComparison.OrdinalIgnoreCase))) yield return n;
        foreach (var n in RequiredGyro)
            if (!def.Gyro.Any(g => g.Axis.Equals(n, StringComparison.OrdinalIgnoreCase))) yield return "gyro." + n;
    }
}
