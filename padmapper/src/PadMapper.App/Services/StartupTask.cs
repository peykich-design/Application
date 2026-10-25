using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using PadMapper.Core.Logging;
using PadMapper.Core.Startup;

namespace PadMapper.App.Services;

/// <summary>
/// ログオン時の自動起動（タスクスケジューラ）。Windows 標準の schtasks.exe に XML を渡して登録する（外部ツールは使わない）。
/// 登録時だけ UAC で昇格し、以降はタスクから「最上位の特権」で UAC なしに起動する。
/// </summary>
internal static class StartupTask
{
    public const string RegisterArg = "--register-task";
    public const string UnregisterArg = "--unregister-task";

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static readonly Lazy<bool> Elevated = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    /// <summary>このプロセスが管理者として（昇格して）動いているか。</summary>
    public static bool IsElevated => Elevated.Value;

    public enum TaskState { NotRegistered, Registered, RegisteredOtherPath, Unknown }

    public static TaskState Query(out string? command)
    {
        command = null;
        var (code, output) = Run($"/Query /TN \"{TaskXmlBuilder.TaskName}\" /XML");
        if (code != 0) return output.Length == 0 ? TaskState.Unknown : TaskState.NotRegistered;
        command = TaskXmlBuilder.ReadCommand(output);
        if (command == null) return TaskState.Unknown;
        return string.Equals(Path.GetFullPath(command), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase)
            ? TaskState.Registered
            : TaskState.RegisteredOtherPath;
    }

    /// <summary>この exe を登録する（管理者権限が必要）。</summary>
    public static bool Register(ILog log)
    {
        var xml = TaskXmlBuilder.Build(ExePath, WindowsIdentity.GetCurrent().Name);
        var tmp = Path.Combine(Path.GetTempPath(), $"padmapper-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            var (code, output) = Run($"/Create /TN \"{TaskXmlBuilder.TaskName}\" /XML \"{tmp}\" /F");
            log.Info($"自動起動タスク登録: exit={code} {output.Trim()}");
            return code == 0;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static bool Unregister(ILog log)
    {
        var (code, output) = Run($"/Delete /TN \"{TaskXmlBuilder.TaskName}\" /F");
        log.Info($"自動起動タスク削除: exit={code} {output.Trim()}");
        return code == 0;
    }

    /// <summary>登録済みのタスクを今すぐ実行する（UAC なしで管理者として起動するため）。</summary>
    public static bool RunNow(ILog log)
    {
        var (code, output) = Run($"/Run /TN \"{TaskXmlBuilder.TaskName}\"");
        log.Info($"自動起動タスク実行: exit={code} {output.Trim()}");
        return code == 0;
    }

    /// <summary>昇格していなければ、UAC で昇格した自分自身に登録・削除をさせる。</summary>
    public static bool SetEnabled(bool enable, ILog log)
    {
        if (IsElevated) return enable ? Register(log) : Unregister(log);
        try
        {
            var psi = new ProcessStartInfo(ExePath, enable ? RegisterArg : UnregisterArg)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(60_000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // UAC でキャンセルされた
            log.Info($"昇格がキャンセルされました: {ex.Message}");
            return false;
        }
    }

    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static (int code, string output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // schtasks はコンソールのコードページ（日本語環境では CP932）で出力する
                StandardOutputEncoding = OemEncoding(),
                StandardErrorEncoding = OemEncoding(),
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(); } catch { }
                return (-1, "timeout");
            }
            return (p.ExitCode, stdout.Result + stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
