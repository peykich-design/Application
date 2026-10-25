using System.Text;

namespace PadMapper.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string m) => log.Write(LogLevel.Debug, m);
    public static void Info(this ILog log, string m) => log.Write(LogLevel.Info, m);
    public static void Warn(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Warn, m, ex);
    public static void Error(this ILog log, string m, Exception? ex = null) => log.Write(LogLevel.Error, m, ex);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message, Exception? ex = null) { }
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// 簡易ファイルロガー。padmapper-yyyyMMdd.log に追記し、7日より古いファイルは消す。
/// 直近の警告・エラーは診断画面に出すためメモリにも残す。
/// </summary>
public sealed class FileLogger : ILog, IDisposable
{
    private readonly string _dir;
    private readonly int _keepDays;
    private readonly Func<DateTime> _now;
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _recentProblems = new();
    private StreamWriter? _writer;
    private DateTime _currentDay;

    public LogLevel MinLevel { get; set; } = LogLevel.Info;
    public string Directory => _dir;

    public FileLogger(string dir, int keepDays = 7, Func<DateTime>? now = null)
    {
        _dir = dir;
        _keepDays = keepDays;
        _now = now ?? (() => DateTime.Now);
        System.IO.Directory.CreateDirectory(dir);
        Cleanup();
    }

    public IReadOnlyList<LogEntry> RecentProblems
    {
        get { lock (_lock) return _recentProblems.ToList(); }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        if (level < MinLevel) return;
        var now = _now();
        var line = new StringBuilder()
            .Append(now.ToString("HH:mm:ss.fff")).Append(' ')
            .Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ')
            .Append(message);
        if (ex != null) line.Append(" | ").Append(ex);
        lock (_lock)
        {
            if (level >= LogLevel.Warn)
            {
                _recentProblems.AddLast(new LogEntry(now, level, ex == null ? message : $"{message}: {ex.Message}"));
                while (_recentProblems.Count > 20) _recentProblems.RemoveFirst();
            }
            try
            {
                if (_writer == null || now.Date != _currentDay)
                {
                    _writer?.Dispose();
                    _currentDay = now.Date;
                    var path = Path.Combine(_dir, $"padmapper-{now:yyyyMMdd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };
                    Cleanup();
                }
                _writer.WriteLine(line.ToString());
            }
            catch
            {
                // ログが書けなくても本体は止めない
            }
        }
    }

    /// <summary>保持期間より古いログファイルを消す。</summary>
    internal void Cleanup()
    {
        try
        {
            var limit = _now().Date.AddDays(-_keepDays);
            foreach (var f in System.IO.Directory.GetFiles(_dir, "padmapper-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParseExact(name.Replace("padmapper-", ""), "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day) && day < limit)
                    File.Delete(f);
            }
        }
        catch
        {
            // 削除に失敗しても続行
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
