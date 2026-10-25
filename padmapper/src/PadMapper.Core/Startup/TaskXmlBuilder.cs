using System.Xml.Linq;

namespace PadMapper.Core.Startup;

/// <summary>
/// ログオン時に管理者権限で起動するためのタスクスケジューラ用 XML（schtasks /Create /XML で登録）。
/// SPEC 9章：ログオン時トリガー、最上位の特権、実行時間制限なし、複数起動は無視、
/// 失敗時に1分間隔で最大3回再起動、バッテリー駆動でも開始・停止しない。
/// </summary>
public static class TaskXmlBuilder
{
    public const string TaskName = "PadMapper";
    public const string AutostartArgument = "--autostart";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(string exePath, string userId, string? description = null)
    {
        XElement E(string name, params object[] content) => new(Ns + name, content);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            E("Task", new XAttribute("version", "1.2"),
                E("RegistrationInfo",
                    E("Description", description ?? "PadMapper をログオン時に管理者権限で起動します（UAC の確認なし）。"),
                    E("URI", "\\" + TaskName)),
                E("Triggers",
                    E("LogonTrigger",
                        E("Enabled", "true"),
                        E("UserId", userId))),
                E("Principals",
                    E("Principal", new XAttribute("id", "Author"),
                        E("UserId", userId),
                        E("LogonType", "InteractiveToken"),
                        E("RunLevel", "HighestAvailable"))),
                E("Settings",
                    E("MultipleInstancesPolicy", "IgnoreNew"),
                    E("DisallowStartIfOnBatteries", "false"),
                    E("StopIfGoingOnBatteries", "false"),
                    E("AllowHardTerminate", "true"),
                    E("StartWhenAvailable", "false"),
                    E("RunOnlyIfNetworkAvailable", "false"),
                    E("IdleSettings",
                        E("StopOnIdleEnd", "false"),
                        E("RestartOnIdle", "false")),
                    E("AllowStartOnDemand", "true"),
                    E("Enabled", "true"),
                    E("Hidden", "false"),
                    E("RunOnlyIfIdle", "false"),
                    E("WakeToRun", "false"),
                    E("ExecutionTimeLimit", "PT0S"),
                    E("Priority", "4"),
                    E("RestartOnFailure",
                        E("Interval", "PT1M"),
                        E("Count", "3"))),
                E("Actions", new XAttribute("Context", "Author"),
                    E("Exec",
                        E("Command", exePath),
                        E("Arguments", AutostartArgument),
                        E("WorkingDirectory", Path.GetDirectoryName(exePath) ?? "")))));

        using var writer = new Utf16StringWriter();
        doc.Save(writer);
        return writer.ToString();
    }

    /// <summary>登録済みタスクの XML から、起動する exe のパスを取り出す（別の場所の exe を指していないかの確認用）。</summary>
    public static string? ReadCommand(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants(Ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    private sealed class Utf16StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.Unicode;
    }
}
