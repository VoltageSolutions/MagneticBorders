using MagneticBorders.Settings.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MagneticBorders.Settings;

/// <summary>
/// Application root of the settings app (MagneticBorders.Settings.exe). Owns the single shared
/// SettingsViewModel, the 2 second status poll, and the lifetime of the one MainWindow and the one
/// FlyoutWindow. Created by Program.Main on the UI thread; all members except OnRedirectedActivation
/// run on that thread.
/// </summary>
public partial class App : Application
{
    /// <summary>The running App, so static entry points (redirected activation, flyout button) can reach it.</summary>
    private static App? _instance;
    /// <summary>UI-thread dispatcher queue, used to marshal redirected activations back onto the UI thread.</summary>
    private DispatcherQueue? _queue;
    /// <summary>Fires every 2 seconds and refreshes the engine status for whichever window is open.</summary>
    private DispatcherTimer? _poll;
    /// <summary>The full settings window, or null when it is closed.</summary>
    private MainWindow? _main;
    /// <summary>The tray flyout, or null when it is closed.</summary>
    private FlyoutWindow? _flyout;
    /// <summary>When the flyout last closed; used to ignore the tray click that deactivated it.</summary>
    private DateTime _flyoutClosedAt = DateTime.MinValue;

    /// <summary>The single view model shared by the flyout and the main window (real settings file, real pipe client).</summary>
    public static SettingsViewModel ViewModel { get; } = new(new SettingsStore(), new PipeEngineClient(), appVersion: AppInfo.FromEntryAssembly().Version);

    /// <summary>Stores the instance and loads App.xaml (theme resources).</summary>
    public App()
    {
        _instance = this;
        UnhandledException += (_, e) => CrashLog.Write("UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write("UnobservedTask", e.Exception);
        InitializeComponent();
    }

    /// <summary>
    /// Starts the status poll and shows the flyout or the window depending on the launch arguments.
    /// Runs on the UI thread.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += async (_, _) => await ViewModel.RefreshAsync();
        _poll.Start();
        Show(Program.WantsFlyout(AppInstance.GetCurrent().GetActivatedEventArgs()));
    }

    /// <summary>Called on a background thread when a second launch forwards its arguments to us.</summary>
    public static void OnRedirectedActivation(AppActivationArguments e)
    {
        var flyout = Program.WantsFlyout(e);
        _instance?._queue?.TryEnqueue(() => _instance.Show(flyout));
    }

    /// <summary>Refreshes engine status, then shows the flyout or the main window. UI thread only.</summary>
    private void Show(bool flyout)
    {
        _ = ViewModel.RefreshAsync();
        if (flyout) ShowFlyout(); else ShowMain();
    }

    /// <summary>Creates the main window if needed and brings it to the foreground.</summary>
    public void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Bring();
    }

    /// <summary>
    /// Toggles the flyout: a second request while it is open closes it, and a request within 450 ms of
    /// it closing is ignored (that is the tray click that just deactivated it).
    /// </summary>
    private void ShowFlyout()
    {
        if (_flyout is not null) { _flyout.Close(); return; }   // tray click while open toggles it
        // A tray click deactivates (and closes) the flyout just before the new launch arrives; don't reopen it.
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(450)) return;
        _flyout = new FlyoutWindow();
        _flyout.Closed += (_, _) => { _flyout = null; _flyoutClosedAt = DateTime.UtcNow; };
        _flyout.ShowNearTray();
    }

    /// <summary>Called by FlyoutWindow to open the full window.</summary>
    public static void OpenSettingsFromFlyout() => _instance?.ShowMain();
}
