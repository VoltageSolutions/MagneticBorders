using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MagneticBorders.Settings.Core;

/// <summary>
/// The one view model behind both the flyout and the full window (CommunityToolkit.Mvvm source generators
/// produce the properties and commands). Changing a setting marks it dirty and schedules a save after the
/// debounce delay on a System.Threading.Timer thread; FlushSave writes under a lock. Status polling and
/// engine commands are awaited with ConfigureAwait(true), so their continuations (and the property changes
/// that follow) run back on the UI thread. No WinUI types, so it is unit-testable.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Where settings.json is read and written.</summary>
    private readonly ISettingsStore _store;
    /// <summary>Pipe client used for status and quit.</summary>
    private readonly IEngineClient _engine;
    /// <summary>One-shot debounce timer; fires FlushSave on a thread-pool thread.</summary>
    private readonly Timer _saveTimer;
    /// <summary>Debounce delay in milliseconds (300 in the app).</summary>
    private readonly int _debounceMs;
    /// <summary>Guards reload and save so they do not interleave.</summary>
    private readonly object _saveGate = new();
    /// <summary>Settings as last loaded or saved.</summary>
    private AppSettings _current = new();
    /// <summary>File write time at the last load or save, to spot edits made by someone else.</summary>
    private DateTime _loadedWriteUtc;
    /// <summary>True while ReloadFromDisk assigns properties, so those changes do not schedule a save.</summary>
    private bool _loading;
    /// <summary>True when there are unsaved changes.</summary>
    private bool _dirty;

    /// <summary>Creates the view model and loads settings.json. appVersion defaults to the entry assembly version.</summary>
    public SettingsViewModel(ISettingsStore store, IEngineClient engine, int debounceMs = 300, string? appVersion = null)
    {
        AppVersion = appVersion ?? AppInfo.FromEntryAssembly().Version;
        _store = store;
        _engine = engine;
        _debounceMs = debounceMs;
        _saveTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
        ReloadFromDisk();
    }

    /// <summary>Application version without build metadata.</summary>
    public string AppVersion { get; }

    /// <summary>Excluded executable names, bound to the list in the main window; changes made through AddApp and RemoveApp schedule a save.</summary>
    public ObservableCollection<string> ExcludedApps { get; } = new();

    /// <summary>Master switch. Generates Enabled; every setting property below saves after the debounce delay.</summary>
    [ObservableProperty] private bool _enabled;
    /// <summary>Start at login. Generates StartAtLogin.</summary>
    [ObservableProperty] private bool _startAtLogin;
    /// <summary>Magnetic coupling on or off. Generates GlueEnabled.</summary>
    [ObservableProperty] private bool _glueEnabled;
    /// <summary>Coupling detection zone in pixels (double because it is bound to a Slider). Generates GlueTolerance.</summary>
    [ObservableProperty] private double _glueTolerance;
    /// <summary>Magnetic attraction on or off. Generates SnapEnabled.</summary>
    [ObservableProperty] private bool _snapEnabled;
    /// <summary>Attraction detection zone in pixels (Slider-bound). Generates SnapDistance.</summary>
    [ObservableProperty] private double _snapDistance;
    /// <summary>Text in the "add excluded app" box. Generates NewApp; not persisted.</summary>
    [ObservableProperty] private string _newApp = "";

    /// <summary>Latest engine status, or null when no engine answered. Generates Status and raises change notifications for the text properties derived from it.</summary>
    [ObservableProperty] private EngineStatus? _status;
    /// <summary>Flyout background. Generates FlyoutBackdrop; FlyoutBackdropIndex mirrors it for a ComboBox.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FlyoutBackdropIndex))] private FlyoutBackdrop _flyoutBackdrop;

    /// <summary>True when the last poll got an answer from an engine.</summary>
    public bool EngineRunning => Status is not null;
    /// <summary>
    /// True when the master switch is on. The coupling and attraction controls bind their IsEnabled to this, so
    /// everything but start at login, excluded apps and the flyout background greys out when Enabled is off.
    /// </summary>
    public bool FeaturesEnabled => Enabled;
    /// <summary>True when the coupling detection zone slider is usable: Enabled and GlueEnabled.</summary>
    public bool CouplingZoneEnabled => Enabled && GlueEnabled;
    /// <summary>True when the attraction detection zone slider is usable: Enabled and SnapEnabled.</summary>
    public bool AttractionZoneEnabled => Enabled && SnapEnabled;

    /// <summary>ComboBox selection of FlyoutBackdrop: 0 translucent, 1 solid. Out-of-range values are ignored.</summary>
    public int FlyoutBackdropIndex
    {
        get => (int)FlyoutBackdrop;
        set { if (value is 0 or 1) FlyoutBackdrop = (FlyoutBackdrop)value; }
    }

    /// <summary>Keeps EngineRunning in step with Status.</summary>
    partial void OnStatusChanged(EngineStatus? value) => OnPropertyChanged(nameof(EngineRunning));

    /// <summary>Change hooks for the setting properties: these and the ones below them schedule a debounced save.</summary>
    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(FeaturesEnabled));
        OnPropertyChanged(nameof(CouplingZoneEnabled));
        OnPropertyChanged(nameof(AttractionZoneEnabled));
        Touch();
    }
    partial void OnStartAtLoginChanged(bool value) => Touch();
    partial void OnGlueEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CouplingZoneEnabled));
        Touch();
    }
    partial void OnGlueToleranceChanged(double value) => Touch();
    partial void OnSnapEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(AttractionZoneEnabled));
        Touch();
    }
    partial void OnFlyoutBackdropChanged(FlyoutBackdrop value) => Touch();
    partial void OnSnapDistanceChanged(double value) => Touch();

    /// <summary>Re-reads settings.json into the view model without scheduling a save.</summary>
    public void ReloadFromDisk()
    {
        lock (_saveGate)
        {
            _loading = true;
            try
            {
                _current = _store.Load();
                _loadedWriteUtc = _store.LastWriteUtc;
                Enabled = _current.Enabled;
                StartAtLogin = _current.StartAtLogin;
                GlueEnabled = _current.GlueEnabled;
                GlueTolerance = _current.GlueTolerancePx;
                SnapEnabled = _current.SnapEnabled;
                SnapDistance = _current.SnapDistancePx;
                FlyoutBackdrop = _current.FlyoutBackdrop;
                ExcludedApps.Clear();
                foreach (var a in _current.ExcludedApps) ExcludedApps.Add(a);
            }
            finally { _loading = false; }
        }
    }

    /// <summary>Marks settings dirty and restarts the debounce timer; ignored while loading.</summary>
    private void Touch()
    {
        if (_loading) return;
        _dirty = true;
        _saveTimer.Change(_debounceMs, Timeout.Infinite);
    }

    /// <summary>Writes pending changes now (also used by tests and on shutdown).</summary>
    public void FlushSave()
    {
        lock (_saveGate)
        {
            if (!_dirty) return;
            _dirty = false;
            var s = _store.Load();
            s.Enabled = Enabled;
            s.StartAtLogin = StartAtLogin;
            s.GlueEnabled = GlueEnabled;
            s.GlueTolerancePx = AppSettings.ClampZone((int)Math.Round(GlueTolerance));
            s.SnapEnabled = SnapEnabled;
            s.SnapDistancePx = AppSettings.ClampZone((int)Math.Round(SnapDistance));
            s.FlyoutBackdrop = FlyoutBackdrop;
            s.ExcludedApps = ExcludedApps.ToList();
            _store.Save(s);
            _current = s;
            _loadedWriteUtc = _store.LastWriteUtc;
        }
    }

    /// <summary>
    /// Polls the engine with "status" and stores the result in Status (null on failure); also reloads
    /// settings.json when someone else changed it and nothing is pending. Generates RefreshCommand. Called by
    /// the 2 second poll in App and by the Refresh button.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!_dirty && _store.LastWriteUtc != _loadedWriteUtc) ReloadFromDisk();
        var r = await _engine.SendAsync("status").ConfigureAwait(true);
        Status = r.Ok ? r.Status : null;
    }

    /// <summary>Sends "quit", waits 400 ms, then refreshes. Generates QuitEngineCommand.</summary>
    [RelayCommand]
    public async Task QuitEngineAsync()
    {
        await _engine.SendAsync("quit").ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Adds NewApp to ExcludedApps (appends ".exe" when there is no dot; skips case-insensitive duplicates) and clears the box. Generates AddAppCommand.</summary>
    [RelayCommand]
    public void AddApp()
    {
        var name = NewApp.Trim();
        if (name.Length == 0) return;
        if (!name.Contains('.')) name += ".exe";
        if (!ExcludedApps.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
        {
            ExcludedApps.Add(name);
            Touch();
        }
        NewApp = "";
    }

    /// <summary>Removes an exe name from ExcludedApps. Generates RemoveAppCommand.</summary>
    [RelayCommand]
    public void RemoveApp(string? name)
    {
        if (name is not null && ExcludedApps.Remove(name)) Touch();
    }

    /// <summary>Stops the timer and writes any pending change.</summary>
    public void Dispose()
    {
        _saveTimer.Dispose();
        FlushSave();
    }
}
