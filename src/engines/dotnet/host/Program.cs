using MagneticBorders.Application;
using MagneticBorders.Host;
using MagneticBorders.Win32;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ITimer = MagneticBorders.Application.ITimer;

bool console = args.Contains("--console");
if (console) Native.AllocConsole();

using var mutex = new Mutex(true, @"Local\MagneticBorders.Instance", out bool first);
if (!first)
{
    Native.Message("Magnetic Borders is already running.", "Magnetic Borders");
    return 1;
}

bool verbose = args.Contains("--verbose");
using var log = new EngineLog(console, verbose);
var appInfo = AppInfo.FromEntryAssembly();
log.Info($"Magnetic Borders (.NET, Native AOT) {appInfo.Version}");
if (!MessageLoop.FullWindowDragEnabled())
    log.Info("WARNING: 'Show window contents while dragging' is off; live magnetic coupling will not work.");

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
var s = builder.Services;
s.AddSingleton<ILog>(log);
s.AddSingleton<IAppInfo>(appInfo);
s.AddSingleton<Win32Timer>();
s.AddSingleton<ITimer>(p => p.GetRequiredService<Win32Timer>());
s.AddSingleton<IClock, StopwatchClock>();
s.AddSingleton<WindowState>();
s.AddSingleton(p => new Win32Inventory(p.GetRequiredService<WindowState>(), []));
s.AddSingleton<IWindowInventory>(p => p.GetRequiredService<Win32Inventory>());
s.AddSingleton(p => new Win32PointerHook(p.GetRequiredService<ILog>()));
s.AddSingleton<IWindowMover>(p => new Win32Mover(p.GetRequiredService<WindowState>(), p.GetRequiredService<ILog>()));
s.AddSingleton(p =>
{
    var c = new DragController(
        p.GetRequiredService<IWindowInventory>(), p.GetRequiredService<IWindowMover>(),
        p.GetRequiredService<IClock>(), p.GetRequiredService<ILog>(), p.GetRequiredService<ITimer>(), 12, "net");
    c.Hook = p.GetRequiredService<Win32PointerHook>();
    p.GetRequiredService<Win32Timer>().Fired = c.OnTimer;
    return c;
});
s.AddSingleton(p =>
{
    var inv = p.GetRequiredService<Win32Inventory>();
    var l = p.GetRequiredService<ILog>();
    return new EngineState(p.GetRequiredService<DragController>(), p.GetRequiredService<IClock>(), l, p.GetRequiredService<IAppInfo>(), Environment.ProcessId,
        inv.SetExcluded, on => Autostart.Set(on, l));
});
s.AddSingleton(p => new WinEventSource(p.GetRequiredService<DragController>(), p.GetRequiredService<ILog>()));
s.AddHostedService<UiThreadService>();

using var host = builder.Build();
await host.RunAsync();
return 0;
