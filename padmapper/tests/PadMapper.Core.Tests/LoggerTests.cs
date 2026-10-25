using PadMapper.Core.Logging;

namespace PadMapper.Core.Tests;

public class LoggerTests
{
    [Fact]
    public void WritesDailyFile_AndDeletesOldOnes()
    {
        using var dir = new TempDir();
        var old = Path.Combine(dir.Path, "padmapper-20200101.log");
        File.WriteAllText(old, "old");
        var now = new DateTime(2026, 10, 25, 12, 0, 0);
        using (var log = new FileLogger(dir.Path, 7, () => now))
        {
            log.Info("hello");
            log.Error("bad", new InvalidOperationException("boom"));
            Assert.Single(log.RecentProblems);
        }
        Assert.False(File.Exists(old));
        var text = File.ReadAllText(Path.Combine(dir.Path, "padmapper-20261025.log"));
        Assert.Contains("hello", text);
        Assert.Contains("boom", text);
    }
}
