using MagneticBorders.Settings.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using static MagneticBorders.Settings.NativeMethods;

namespace MagneticBorders.Settings;

/// <summary>
/// Always-on-top tray quick-access flyout (about 360 x 300 epx), anchored bottom-right of the work area of the
/// monitor under the cursor. It has no XAML border: Windows draws the border (a fixed neutral grey for the light or dark
/// theme, never the accent color), the rounded corners and the shadow. The background, acrylic or solid, comes from the
/// "flyout background" setting and is applied each time the flyout opens. Binds to the shared App.ViewModel
/// (see FlyoutWindow.xaml). Closes on Escape, on losing activation, or through a 250 ms focus-watch timer when
/// Windows refuses foreground. Created and closed on the UI thread by App.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    /// <summary>Border colors as COLORREF (0x00BBGGRR): a subtle grey edge on the dark and the light flyout.</summary>
    private const int BorderDark = 0x00404040, BorderLight = 0x00D9D9D9;
    /// <summary>Flyout size and distance from the work-area edges, in effective pixels (scaled by monitor DPI).</summary>
    private const double WidthEpx = 360, HeightEpx = 340, MarginEpx = 12;
    /// <summary>Native window handle, used for DPI, foreground and DWM calls.</summary>
    private readonly nint _hwnd;
    /// <summary>Closes the flyout when it is no longer the foreground window (fallback for a missed Deactivated event).</summary>
    private readonly DispatcherQueueTimer _focusWatch;
    /// <summary>When the flyout was shown; deactivations right after showing are ignored.</summary>
    private DateTime _shownAt;
    /// <summary>Set once a close has started so Close is only called once.</summary>
    private bool _closing;

    /// <summary>Shared view model, the source for x:Bind in FlyoutWindow.xaml.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "x:Bind needs an instance property.")]
    public SettingsViewModel ViewModel => App.ViewModel;

    /// <summary>Strips the title bar, makes the window non-resizable and topmost, asks DWM for rounded corners, the default border colour and a system shadow, and wires the close-on-focus-loss logic.</summary>
    public FlyoutWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        AppWindow.IsShownInSwitchers = false;

        Title = Fmt.Get("WindowTitle");

        // Create the presenter rather than test `AppWindow.Presenter is OverlappedPresenter`: in the Native AOT
        // build that type test returns false, which left the default title bar and border on the flyout.
        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false);
        p.IsResizable = false;
        p.IsMaximizable = false;
        p.IsMinimizable = false;
        p.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(p);

        var round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        // A fixed neutral border for the theme. DWMWA_COLOR_DEFAULT would follow "Show accent color on title bars and
        // window borders", which draws the flyout's edge in the accent color.
        var borderColor = Application.Current.RequestedTheme == ApplicationTheme.Dark ? BorderDark : BorderLight;
        _ = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        var margins = new MARGINS { Bottom = 1 };   // a one pixel glass edge makes DWM draw the system shadow
        _ = DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        Activated += (_, e) =>
        {
            StartupProbe(e);
            if (e.WindowActivationState == WindowActivationState.Deactivated && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(150))
                CloseSafe();
        };

        // Fallback for when Windows refuses to give us foreground (so Deactivated would never fire).
        _focusWatch = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _focusWatch.Interval = TimeSpan.FromMilliseconds(250);
        _focusWatch.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(800) && GetForegroundWindow() != _hwnd) CloseSafe();
        };
        Closed += (_, _) => _focusWatch.Stop();
    }

    /// <summary>Debug builds only: writes the time from process start to the first activation to the debugger output. Compiled out of Release.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void StartupProbe(WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated || _probed) return;
        _probed = true;
        var ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        System.Diagnostics.Debug.WriteLine($"Flyout first activation {ms:F0} ms after process start");
    }

    private static bool _probed;

    /// <summary>Applies the flyout background setting: acrylic for translucent, or no backdrop plus the opaque theme fill for solid.</summary>
    private void ApplyBackdrop()
    {
        var solid = ViewModel.FlyoutBackdrop == FlyoutBackdrop.Solid;
        SystemBackdrop = solid ? null : new DesktopAcrylicBackdrop();
        SolidFill.Visibility = solid ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Applies the background setting, positions the flyout in the bottom-right of the work area under the
    /// cursor, sized for that monitor's DPI, then shows and activates it. UI thread only.
    /// </summary>
    public void ShowNearTray()
    {
        ApplyBackdrop();
        GetCursorPos(out var pt);
        var area = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        // Move first so the window picks up the target monitor's DPI, then size in physical pixels.
        AppWindow.Move(new PointInt32(work.X, work.Y));
        var scale = ScaleFor(_hwnd);
        int w = (int)Math.Round(WidthEpx * scale), h = (int)Math.Round(HeightEpx * scale), m = (int)Math.Round(MarginEpx * scale);
        h = Math.Min(h, work.Height - 2 * m);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - m, work.Y + work.Height - h - m, w, h));

        _shownAt = DateTime.UtcNow;
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
        _focusWatch.Start();
    }

    /// <summary>Closes the window once; repeated calls are no-ops.</summary>
    private void CloseSafe()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>Escape closes the flyout.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) CloseSafe();
    }

    /// <summary>Click handler of the "Open settings" button: opens the main window first (keeps the process alive), then closes the flyout.</summary>
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        App.OpenSettingsFromFlyout();   // open the window first so the process stays alive
        CloseSafe();
    }
}
