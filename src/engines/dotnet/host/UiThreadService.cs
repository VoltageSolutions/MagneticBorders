using MagneticBorders.Application;
using MagneticBorders.Win32;
using Microsoft.Extensions.Hosting;

namespace MagneticBorders.Host;

/// <summary>Runs all Win32 work (hooks, tray window, message loop) on one dedicated thread.</summary>
internal sealed class UiThreadService(
    WinEventSource events, EngineState state, ILog log, IAppInfo app, IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    private Thread? _thread;
    private uint _tid;
    private Exception? _failure;
    private readonly ManualResetEventSlim _ready = new();

    /// <summary>Starts the UI thread and waits until setup finished. Rethrows a setup failure so the host fails to start.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "MagneticBorders.UI" };
        _thread.Start();
        _ready.Wait(ct);
        if (_failure is not null) throw _failure;
        return Task.CompletedTask;
    }

    /// <summary>Thread body: creates the tray, wires events, installs hooks, starts the watcher and pipe, then runs the message loop until WM_QUIT and tears everything down.</summary>
    private void Run()
    {
        TrayHost? tray = null;
        SettingsWatcher? watcher = null;
        PipeServer? pipe = null;
        try
        {
            _tid = MessageLoop.CurrentThreadId;
            MessageLoop.EnsureQueue();

            void Reload()
            {
                var m = SettingsStore.Load(log);
                if (m is not null) state.Apply(m);
            }

            tray = new TrayHost("Magnetic Borders", $"Magnetic Borders {app.Version}", log,
                () => state.Settings.Enabled, () => SettingsStore.ToggleEnabled(log), Reload);
            tray.Create();
            var t = tray;
            state.Changed += t.Refresh;
            state.ReloadRequested = Reload;
            state.QuitRequested = t.QuitSoon;
            Reload();
            events.Install();
            watcher = new SettingsWatcher(() => t.Post(t.DebounceReload));
            pipe = new PipeServer(line => t.Invoke(() => state.Handle(line), 500) ?? "{\"ok\":false,\"error\":\"timeout\"}", log);
            log.Info($"engine started pid={Environment.ProcessId} version={app.Version}");
        }
        catch (Exception ex) { _failure = ex; _ready.Set(); return; }
        _ready.Set();
        try { MessageLoop.Run(); }
        finally
        {
            pipe?.Dispose();
            watcher?.Dispose();
            tray?.Dispose();
            events.Dispose();
            log.Info("hooks removed, exiting");
            lifetime.StopApplication();
        }
    }

    /// <summary>Releases the setup wait handle.</summary>
    public void Dispose() => _ready.Dispose();

    /// <summary>Posts WM_QUIT to the UI thread and waits up to 3 seconds for it to finish.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        if (_thread is { IsAlive: true })
        {
            MessageLoop.PostQuit(_tid);
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        return Task.CompletedTask;
    }
}
