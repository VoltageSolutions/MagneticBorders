namespace MagneticBorders.Application;

/// <summary>Runtime state: applies settings to the controller and executes pipe commands. UI thread only.</summary>
public sealed class EngineState(
    DragController controller, IClock clock, ILog log, IAppInfo app, int pid,
    Action<IReadOnlyList<string>> setExcluded, Action<bool> setAutostart)
{
    private readonly ulong _start = clock.NowMs();
    private bool _snapLogged;

    /// <summary>Settings last applied (defaults until the first Apply).</summary>
    public SettingsModel Settings { get; private set; } = new();

    /// <summary>Raised after settings changed (tray tooltip/menu refresh).</summary>
    public event Action? Changed;
    /// <summary>Host re-reads settings.json and calls Apply.</summary>
    public Action? ReloadRequested { get; set; }
    /// <summary>Set by the host; invoked for the pipe command quit.</summary>
    public Action? QuitRequested { get; set; }

    /// <summary>True when the master switch and magnetic coupling are both enabled in settings.</summary>
    public bool GlueActive => Settings.Enabled && Settings.GlueEnabled;

    /// <summary>Applies a settings model: tolerance, active flag, excluded apps and autostart, then raises Changed.</summary>
    public void Apply(SettingsModel m)
    {
        Settings = m;
        controller.TolerancePx = m.GlueTolerancePx;
        controller.Active = GlueActive;
        setExcluded(m.ExcludedApps);
        setAutostart(m.StartAtLogin);
        if (m.SnapEnabled && !_snapLogged) { log.Info("snap: not implemented"); _snapLogged = true; }
        if (!m.SnapEnabled) _snapLogged = false;
        log.Info($"settings applied: enabled={m.Enabled} magnetic_coupling={m.GlueEnabled} tolerance_px={m.GlueTolerancePx} excluded=[{string.Join(",", m.ExcludedApps)}] start_at_login={m.StartAtLogin}");
        Changed?.Invoke();
    }

    /// <summary>Builds the status reply data from current state and controller counters.</summary>
    public StatusSnapshot Status() => new(
        EngineInfo.Name, app.Version, pid, Settings.Enabled, Settings.GlueEnabled, Settings.GlueTolerancePx,
        controller.Drags, controller.GluedDrags, (long)((clock.NowMs() - _start) / 1000));

    /// <summary>Handles one request line and returns the response line.</summary>
    public string Handle(string line)
    {
        var r = PipeProtocol.Handle(line, Status());
        switch (r.Action)
        {
            case PipeAction.Reload: ReloadRequested?.Invoke(); break;
            case PipeAction.Quit: QuitRequested?.Invoke(); break;
        }
        return r.Response;
    }
}
