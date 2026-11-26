using MagneticBorders.Settings.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;
using static MagneticBorders.Settings.NativeMethods;

namespace MagneticBorders.Settings;

/// <summary>
/// Full settings window (about 920 x 780 epx, Mica backdrop, custom title bar). Binds to the shared
/// App.ViewModel (see MainWindow.xaml). Flushes pending saves when it closes. Created by App on the UI thread.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Native window handle, used for DPI and foreground calls.</summary>
    private readonly nint _hwnd;

    /// <summary>Shared view model, the source for x:Bind in MainWindow.xaml.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "x:Bind needs an instance property.")]
    public SettingsViewModel ViewModel => App.ViewModel;

    /// <summary>Sets the title, title bar, icon and DPI-scaled size, and flushes the view model's pending save on close.</summary>
    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = Fmt.Get("WindowTitle");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        var s = ScaleFor(_hwnd);
        AppWindow.Resize(new SizeInt32((int)(920 * s), (int)(780 * s)));
        Closed += (_, _) => ViewModel.FlushSave();
    }

    /// <summary>Restores the window if minimized, shows it and takes the foreground.</summary>
    public void Bring()
    {
        if (IsIconic(_hwnd)) ShowWindow(_hwnd, SW_RESTORE);   // Win32, no presenter type test (unreliable under Native AOT)
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    /// <summary>Remove button in the excluded-apps list; the exe name travels in the button Tag.</summary>
    private void OnRemoveApp(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) ViewModel.RemoveApp(name);
    }

    /// <summary>Enter in the new-app text box runs the Add command.</summary>
    private void OnNewAppKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ViewModel.AddAppCommand.Execute(null);
    }
}
