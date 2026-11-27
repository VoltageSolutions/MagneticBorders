# Troubleshooting

## Which engine is your Magnetic Borders running?

If you grab one of the posted releases for Magnetic Borders, you're running the C++ (cpp) build of the application. You may however have chosen one of the alternative builds (an app engine). When asking for support, be sure to identify which build of the app you're using.

## Find the log

Magnetic Borders writes a log to `%LOCALAPPDATA%\MagneticBorders\logs\<engine>.log`. The `<engine>` part is `cpp`, `rust`, or `dotnet`. The engine trims the file at startup if it grows past 1 MB.

To watch the log live, start the engine from a terminal with `--console`. Add `--verbose` for more detail.

Each drag writes one line with the grabbed edges and the windows that joined. If you report a bug, attach the log and say which engine you ran.

## Coupled windows flicker while I drag, or the flyout has a white border

Both can depend on the graphics hardware, the display scaling, and the Windows transparency and power settings, so the log records them. To capture one:

1. Exit Magnetic Borders from its notification area menu.
1. Start the engine from a terminal with `--verbose`, for example `MagneticBorders.Cpp.exe --verbose`.
1. Drag a shared border until the flicker shows, then open the flyout from the notification area icon until the white border shows. Exit the engine.
1. Attach `%LOCALAPPDATA%\MagneticBorders\logs\<engine>.log` and `%LOCALAPPDATA%\MagneticBorders\logs\settings.log`.

The [engine spec](spec/engine.md#diagnostics) explains each line of the engine log, and the [settings app spec](spec/settings-app.md#diagnostics) explains each line of `settings.log`.

## Nothing happens when I drag

Magnetic Borders follows the location updates Windows sends while you drag. If this Windows option is off, Windows won't send update events until you let go. This prevents Magnetic Borders from working. MB will write a warning to its log at startup if this option is off.

You can reconfigure this in Advanced System Properties.

1. Open System properties, either from the Settings app or the keystroke Windows key + Pause.
1. Select **Advanced system settings**.
1. Select the **Advanced** tab, and in the **Performance** section, click on **Settings**.
1. On the **Visual Effects** tab, ensure to check **Show window contents while dragging**.

## Elevated administrative applications

Windows blocks a normal program from moving an application window elevated to administrator. Windows "refuses" Magnetic Borders from changing the app window's size. When this happens, MB selects this app as excluded until MB restarts. The other windows keep working.

To couple an elevated window, you must elevate Magnetic Borders. This is risky and I don't recommend it.

## A neighbor stops before my window does

The neighbor reached its minimum or maximum size.

## I can't start a second instance of Magnetic Borders

And you shouldn't! Only one at at time. If you're intending to test different engines, shut down the first instance of MB.
