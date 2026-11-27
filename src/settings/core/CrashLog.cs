using System.Globalization;

namespace MagneticBorders.Settings.Core;

/// <summary>
/// Appends unhandled exceptions to %LOCALAPPDATA%\MagneticBorders\logs\settings.log, so a crash leaves a record.
/// Never throws: a failure to log is ignored.
/// </summary>
public static class CrashLog
{
    /// <summary>Default log file.</summary>
    public static string DefaultPath => Path.Join(SettingsStore.DataDir, "logs", "settings.log");

    /// <summary>Appends one entry with the time, where it was caught, and the full exception text.</summary>
    public static void Write(string source, Exception? exception, string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {source}: {exception?.ToString() ?? "(no exception)"}{Environment.NewLine}");
            File.AppendAllText(file, text);
        }
#pragma warning disable CA1031 // A crash logger must never throw.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
