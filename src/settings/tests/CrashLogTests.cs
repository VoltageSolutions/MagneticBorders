using MagneticBorders.Settings.Core;
using Xunit;

namespace MagneticBorders.Settings.Tests;

public class CrashLogTests
{
    [Fact]
    public void Write_AppendsTheSourceAndTheException()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mb-crashlog-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, "logs", "settings.log");
        try
        {
            CrashLog.Write("Test", new InvalidOperationException("boom"), file);
            CrashLog.Write("Test", null, file);
            var text = File.ReadAllText(file);
            Assert.Contains("Test: System.InvalidOperationException: boom", text, StringComparison.Ordinal);
            Assert.Contains("(no exception)", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_NeverThrows()
    {
        CrashLog.Write("Test", new InvalidOperationException("boom"), "\0bad");
        Assert.EndsWith("settings.log", CrashLog.DefaultPath, StringComparison.Ordinal);
    }
}
