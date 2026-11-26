using System.Collections.ObjectModel;

// Native AOT: collections handed to WinUI need C#/WinRT interop code generated at build time. These types come from
// outside this project, so the generator only emits that code when they are listed here. Without it, opening the
// settings window fails with "Argument 'source' is not a supported vector" when a SettingsExpander passes its items
// to its ItemsRepeater.
//   ObservableCollection<string>: the Excluded apps list (SettingsViewModel.ExcludedApps, bound to ItemsSource).
//   List<object>: SettingsExpander.Items, used by the Magnetic Borders status expander.
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(ObservableCollection<string>))]
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(List<object>))]
