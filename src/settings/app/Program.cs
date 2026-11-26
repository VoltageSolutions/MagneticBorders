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

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }

    /// <summary>True when a launch activation carries the --flyout argument (case-insensitive); any other activation means the full window.</summary>
    public static bool WantsFlyout(AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is ILaunchActivatedEventArgs l)
            return l.Arguments?.Contains("--flyout", StringComparison.OrdinalIgnoreCase) == true;
        return false;
    }
}
