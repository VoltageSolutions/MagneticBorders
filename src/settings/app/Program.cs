using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace MagneticBorders.Settings;

/// <summary>
/// Process entry point. Enforces a single instance with AppInstance key redirection, then starts the
/// WinUI Application on an STA thread. A second launch forwards its arguments to the first and exits.
/// </summary>
public static class Program
{
    /// <summary>AppInstance key shared by all launches of this app.</summary>
    private const string InstanceKey = "MagneticBorders.Settings.Main";

    /// <summary>
    /// Registers for the instance key. If another instance owns it, redirects this launch's activation
    /// to it (on a worker task, blocking Main until done) and returns 0. Otherwise starts the app and routes
    /// redirected activations to App.OnRedirectedActivation.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Smoke test: no single-instance redirection, so a running copy never takes over the test.
        int smoke = Array.IndexOf(args, "--smoke");
        if (smoke >= 0)
        {
            SmokeDir = smoke + 1 < args.Length ? args[smoke + 1] : Path.Join(Path.GetTempPath(), "MagneticBorders-smoke");
            Directory.CreateDirectory(SmokeDir);
            StartApp();
            return ExitCode;
        }

        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            // Another instance owns the key: hand it our activation (flyout or window) and exit.
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            using var done = new ManualResetEventSlim();
            _ = Task.Run(async () =>
            {
                try { await main.RedirectActivationToAsync(activation); }
                finally { done.Set(); }
            });
            done.Wait();
            return 0;
        }

        main.Activated += (_, e) => App.OnRedirectedActivation(e);
        StartApp();
        return ExitCode;
    }

    /// <summary>
    /// Set by <c>--smoke [folder]</c>: the app opens the settings window and the flyout, then exits. Settings and the
    /// crash log go to this folder instead of %LOCALAPPDATA%\MagneticBorders. Null in normal runs.
    /// </summary>
    public static string? SmokeDir { get; private set; }

    /// <summary>Process exit code: 0, or the code App.FailSmoke set.</summary>
    public static int ExitCode { get; set; }

    /// <summary>Runs the WinUI application on this thread until it exits.</summary>
    private static void StartApp()
    {
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
    }

    /// <summary>True when a launch activation carries the --flyout argument (case-insensitive); any other activation means the full window.</summary>
    public static bool WantsFlyout(AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs l)
            return l.Arguments?.Contains("--flyout", StringComparison.OrdinalIgnoreCase) == true;
        return false;
    }
}
