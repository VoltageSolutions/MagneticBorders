using System.IO.Pipes;
using System.Text;
using MagneticBorders.Application;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>Locations of the engine's data files under %LOCALAPPDATA%\MagneticBorders.</summary>
public static class Paths
{
    /// <summary>Folder holding settings.json and logs.</summary>
    public static string DataDir { get; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagneticBorders");
    /// <summary>Path of the shared settings.json.</summary>
    public static string SettingsFile => Path.Join(DataDir, "settings.json");
    /// <summary>Path of this engine's log file.</summary>
    public static string LogFile => Path.Join(DataDir, "logs", EngineInfo.Name + ".log");
}

/// <summary>Manages the HKCU Run entry for start at login.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "MagneticBorders";

    /// <summary>Writes or removes the Run value so it points at the running exe. Failures are logged, not thrown.</summary>
    public static void Set(bool on, ILog log)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (on)
            {
                string? exe = Environment.ProcessPath;
                if (exe is not null && !string.Equals(k.GetValue(Name) as string, exe, StringComparison.Ordinal)) { k.SetValue(Name, exe); log.Info("start_at_login: Run key written"); }
            }
            else if (k.GetValue(Name) is not null) { k.DeleteValue(Name, false); log.Info("start_at_login: Run key removed"); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { log.Info("start_at_login failed: " + ex.Message); }
    }
}

/// <summary>Small CsWin32 calls used before the host is built.</summary>
public static unsafe class Native
{
    /// <summary>Attaches a console window (for --console).</summary>
    public static void AllocConsole() => PInvoke.AllocConsole();

    /// <summary>Shows a modal information message box.</summary>
    public static void Message(string text, string title) =>
        PInvoke.MessageBox(Windows.Win32.Foundation.HWND.Null, text, title, MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
}

/// <summary>Watches settings.json; invokes onChange on a background thread (caller must only post).</summary>
public sealed class SettingsWatcher : IDisposable
{
    private readonly FileSystemWatcher _w;

    /// <summary>Starts watching immediately. onChange is called on a thread-pool thread.</summary>
    public SettingsWatcher(Action onChange)
    {
        Directory.CreateDirectory(Paths.DataDir);
        _w = new FileSystemWatcher(Paths.DataDir, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _w.Changed += (_, _) => onChange();
        _w.Created += (_, _) => onChange();
        _w.Renamed += (_, _) => onChange();
        _w.EnableRaisingEvents = true;
    }

    /// <summary>Stops watching.</summary>
    public void Dispose() => _w.Dispose();
}

/// <summary>Named pipe server; handler is called on a thread-pool thread and must return the response line.</summary>
public sealed class PipeServer : IDisposable
{
    private readonly Func<string, string> _handler;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    /// <summary>Starts the background accept thread. handler runs on a thread-pool thread, so it must hand work to the UI thread (see TrayHost.Invoke).</summary>
    public PipeServer(Func<string, string> handler, ILog log)
    {
        _handler = handler; _log = log;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MagneticBorders.Pipe" };
        _thread.Start();
    }

    /// <summary>Accept loop on the dedicated pipe thread; each connection is served by a task.</summary>
    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? s = null;
            try
            {
                s = new NamedPipeServerStream(PipeProtocol.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                s.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                var conn = s; s = null;
                _ = Task.Run(() => Serve(conn));
            }
            catch (OperationCanceledException) { s?.Dispose(); return; }
            catch (Exception ex) { s?.Dispose(); _log.Info("pipe error: " + ex.Message); Thread.Sleep(200); }
        }
    }

    /// <summary>Reads one request line, calls the handler, and writes one reply line. Limited to 2 seconds per client.</summary>
    private async Task Serve(NamedPipeServerStream s)
    {
        try
        {
            using (s)
            {
                using var to = new CancellationTokenSource(2000);
                using var reader = new StreamReader(s, new UTF8Encoding(false), false, 1024, leaveOpen: true);
                string? line = await reader.ReadLineAsync(to.Token);
                if (line is null) return;
                string resp = _handler(line);
                var bytes = new UTF8Encoding(false).GetBytes(resp + "\n");
                await s.WriteAsync(bytes, to.Token);
                await s.FlushAsync(to.Token);
                // The client may close before reading everything; the reply was already written.
                try { s.WaitForPipeDrain(); }
                catch (IOException ex) { _log.Debug("pipe drain: " + ex.Message); }
            }
        }
        catch (Exception ex) { _log.Info("pipe client error: " + ex.Message); }
    }

    /// <summary>Cancels the accept loop.</summary>
    public void Dispose() => _cts.Cancel();
}

/// <summary>Client side of the pipe, used by a second instance to talk to the running one.</summary>
public static class PipeClient
{
    /// <summary>Sends one request line to a running engine; returns the reply or null.</summary>
    public static string? Request(string line)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut);
            c.Connect(500);
            var enc = new UTF8Encoding(false);
            var b = enc.GetBytes(line + "\n");
            c.Write(b, 0, b.Length);
            c.Flush();
            using var cts = new CancellationTokenSource(1000);
            using var reader = new StreamReader(c, enc);
            return reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return null; }
    }
}
