using System.Globalization;

namespace MagneticBorders.Settings.Core;

/// <summary>
/// Appends diagnostic lines to %LOCALAPPDATA%\MagneticBorders\logs\settings.log, the file CrashLog also writes. Each
/// line starts with the local time. The file is replaced when it passes 1 MB. Never throws: a failure to log is ignored.
/// </summary>
public static class DiagnosticsLog
{
    /// <summary>Size in bytes above which the next write starts a new file.</summary>
    public const long MaxBytes = 1_048_576;

    /// <summary>Appends one timestamped line to the default log file, or to path when given.</summary>
    public static void Write(string message, string? path = null)
    {
        try
        {
            var file = path ?? CrashLog.DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxBytes) File.WriteAllText(file, line);
            else File.AppendAllText(file, line);
        }
#pragma warning disable CA1031 // A diagnostic logger must never throw.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
