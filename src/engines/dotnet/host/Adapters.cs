using MagneticBorders.Application;
using MagneticBorders.Win32;

namespace MagneticBorders.Host;

/// <summary>Logs to the log file (truncated at 1 MB on start) and optionally mirrors to the console.</summary>
internal sealed class EngineLog : ILog, IDisposable
{
    private readonly bool _console, _verbose;
    private readonly Lock _lock = new();
    private readonly StreamWriter? _file;

    /// <summary>Opens the log file (truncating it past 1 MB). Logging continues without a file if it cannot be opened.</summary>
    public EngineLog(bool console, bool verbose)
    {
        _console = console; _verbose = verbose;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.LogFile)!);
            var fi = new FileInfo(Paths.LogFile);
            var mode = fi.Exists && fi.Length > 1_048_576 ? FileMode.Create : FileMode.Append;
            _file = new StreamWriter(new FileStream(Paths.LogFile, mode, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _file = null; }
    }

    /// <summary>Writes one timestamped line to the file and, if enabled, the console. Thread safe.</summary>
    private void Write(string msg)
    {
        lock (_lock)
        {
            // A failed log write must not fail the caller; the console copy below still goes out.
            try { _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}"); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine("log write failed: " + ex.Message); }
            if (_console) Console.WriteLine(msg);
        }
    }

    /// <summary>Closes the log file.</summary>
    public void Dispose()
    {
        lock (_lock) { _file?.Dispose(); }
    }

    /// <summary>Always logged.</summary>
    public void Info(string msg) => Write(msg);
    /// <summary>Logged only with --verbose.</summary>
    public void Debug(string msg) { if (_verbose) Write(msg); }
}

/// <summary>IClock backed by Environment.TickCount64.</summary>
internal sealed class StopwatchClock : IClock
{
    /// <summary>Milliseconds from the tick counter.</summary>
    public ulong NowMs() => (ulong)Environment.TickCount64;
}

/// <summary>File access for settings.json.</summary>
internal static class SettingsStore
{
    /// <summary>Reads settings.json; null when unreadable/invalid (caller keeps previous), defaults when missing.</summary>
    public static SettingsModel? Load(ILog log)
    {
        if (!File.Exists(Paths.SettingsFile)) return new SettingsModel();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string text;
                using (var fs = new FileStream(Paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) text = r.ReadToEnd();
                if (SettingsParser.TryParse(text, out var m, out var err)) return m;
                log.Info("WARNING: settings.json parse error, keeping previous settings: " + err);
                return null;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { log.Info("WARNING: settings.json unreadable: " + ex.Message); return null; }
        }
        log.Info("WARNING: settings.json locked, keeping previous settings");
        return null;
    }

    /// <summary>Flips the saved master switch ("enabled") in settings.json, keeping every other key. The file watcher then applies the change like any other edit.</summary>
    public static void ToggleEnabled(ILog log)
    {
        try
        {
            string text = File.Exists(Paths.SettingsFile) ? File.ReadAllText(Paths.SettingsFile) : "";
            bool current = !SettingsParser.TryParse(text, out var m, out _) || m.Enabled;
            if (!SettingsParser.TryWithEnabled(text, !current, out var result, out var err))
            {
                log.Info("WARNING: could not update settings.json; the Enabled switch was not changed: " + err);
                return;
            }
            string tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, result, new System.Text.UTF8Encoding(false));
            File.Move(tmp, Paths.SettingsFile, overwrite: true);
            log.Info($"Enabled switched {(current ? "off" : "on")} from the tray menu");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Info("WARNING: could not update settings.json; the Enabled switch was not changed: " + ex.Message); }
    }
}
