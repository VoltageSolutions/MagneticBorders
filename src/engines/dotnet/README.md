# Magnetic Borders - .NET build

Native AOT console app (net10.0). Same behaviour as `cpp/` and `rust/`; shared fixtures in `../fixtures`.

| Layer | Project |
|---|---|
| Domain (Rect, Seam, SeamBuilder.Build/Propagate, Attraction, PointerDrag) | `core/` (MagneticBorders.Core) |
| Application (DragController + ports) | `app/` (MagneticBorders.Application) |
| Win32 adapters (CsWin32, WinEvent hooks, mouse hook, DeferWindowPos) | `win32/` (MagneticBorders.Win32) |
| Host (composition root, UI thread, message loop) | `host/` (MagneticBorders.Host) -> `MagneticBorders.Net.exe` |
| Fixture runner | `fixture-runner/` (`--print` supported) |
| Tests (xUnit) | `tests/*` (Core incl. all fixtures; controller with fake ports) |

Dependencies point inward: Host -> Win32 -> Application -> Core. Only Host knows concrete types.
Hook callbacks are `[UnmanagedCallersOnly]` statics routed through one static slot in `WinEventSource` (and one in `Win32PointerHook` for the mouse hook).

## Versus WPF MVVM + DI
- No XAML, views, or view-models: the "UI" is a message loop, so there is nothing to bind.
- The ports (`IWindowInventory`, `IWindowMover`, ...) play the role MVVM services play, but there is no
  INotifyPropertyChanged; the controller is a plain state machine tested with fakes.
- DI is the same Microsoft.Extensions container, but registered explicitly (no reflection scanning) so Native AOT works.
- Win32 work stays on one dedicated thread instead of the WPF Dispatcher.
