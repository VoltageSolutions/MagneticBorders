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
    /// <summary>Smoke-test timers. Held here so the garbage collector cannot collect a timer before it fires.</summary>
    private readonly List<DispatcherQueueTimer> _smokeTimers = [];
    /// <summary>When the flyout last closed; used to ignore the tray click that deactivated it.</summary>
    private DateTime _flyoutClosedAt = DateTime.MinValue;

    /// <summary>The single view model shared by the flyout and the main window (real settings file, real pipe client).</summary>
    public static SettingsViewModel ViewModel { get; } = new(
        new SettingsStore(Program.SmokeDir is null ? null : Path.Join(Program.SmokeDir, "settings.json")),
        new PipeEngineClient(), appVersion: AppInfo.FromEntryAssembly().Version);

    /// <summary>Crash log file: the default one, or settings.log in the smoke-test folder.</summary>
    private static string? CrashLogPath => Program.SmokeDir is null ? null : Path.Join(Program.SmokeDir, "settings.log");

    /// <summary>How long the smoke test lets each window load and lay out before it moves on.</summary>
    private static readonly TimeSpan SmokeStep = TimeSpan.FromSeconds(2);
    /// <summary>The smoke test fails when it has not finished after this long.</summary>
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Stores the instance and loads App.xaml (theme resources).</summary>
    public App()
    {
        _instance = this;
        UnhandledException += (_, e) => Crashed("UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Crashed("UnobservedTask", e.Exception);
        InitializeComponent();
    }

    /// <summary>
    /// Starts the status poll and shows the flyout or the window depending on the launch arguments.
    /// Runs on the UI thread.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        if (Program.SmokeDir is not null)
        {
            RunSmoke();
            return;
        }
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

    /// <summary>Logs an unhandled exception. In a smoke test it also ends the process with exit code 1.</summary>
    private static void Crashed(string source, Exception? exception)
    {
        CrashLog.Write(source, exception, CrashLogPath);
        if (Program.SmokeDir is not null) Environment.Exit(1);
    }

    /// <summary>
    /// Smoke test (<c>--smoke</c>): opens the settings window, then the flyout, letting each load and lay out, and
    /// exits with code 0. The flyout may close itself early when Windows refuses it the foreground; the app then
    /// exits with 0 as soon as it closes, which still means both windows loaded. Any unhandled exception exits with 1 (see Crashed), and not finishing in time exits with 2.
    /// This catches start-up failures that only the published Native AOT build has.
    /// </summary>
    private void RunSmoke()
    {
        var timeout = _queue!.CreateTimer();
        _smokeTimers.Add(timeout);
        timeout.Interval = SmokeTimeout;
        timeout.IsRepeating = false;
        timeout.Tick += (_, _) =>
        {
            CrashLog.Write("Smoke", new TimeoutException("The smoke test did not finish in time."), CrashLogPath);
            Environment.Exit(2);
        };
        timeout.Start();

        ShowMain();
        After(SmokeStep, () =>
        {
            // Flyout first: closing the last open window ends the app.
            ShowFlyout();
            _main?.Close();
            After(SmokeStep, () =>
            {
                _flyout?.Close();
                timeout.Stop();
                Program.ExitCode = 0;
                Exit();
            });
        });
    }

    /// <summary>Runs an action once on the UI thread after a delay.</summary>
    private void After(TimeSpan delay, Action action)
    {
        var t = _queue!.CreateTimer();
        _smokeTimers.Add(t);
        t.Interval = delay;
        t.IsRepeating = false;
        t.Tick += (_, _) => action();
        t.Start();
    }

    /// <summary>Called by FlyoutWindow to open the full window.</summary>
    public static void OpenSettingsFromFlyout() => _instance?.ShowMain();
}
