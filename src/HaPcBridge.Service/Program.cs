using HaPcBridge.Common;
using HaPcBridge.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

const string ServiceName = "HaPcBridge";

switch (args.FirstOrDefault()?.ToLowerInvariant())
{
    case "install": return Installer.Install(ServiceName);
    case "uninstall": return Installer.Uninstall(ServiceName);
    case "init": return Maintenance.Init(args);
    case "purge": return await Maintenance.PurgeAsync();
    case "help" or "-h" or "--help" or "/?":
        Console.WriteLine("""
            HA PC Bridge service
              HaPcBridge.Service.exe install     install and start the Windows service (admin)
              HaPcBridge.Service.exe uninstall   stop and remove the service (admin)
              HaPcBridge.Service.exe init --host H [--port N] [--user U]
                                                 write broker settings (password: env HAPCBRIDGE_MQTT_PASSWORD)
              HaPcBridge.Service.exe purge       remove this PC's retained topics from the broker
              HaPcBridge.Service.exe             run in this console (debugging)
            """);
        return 0;
}

// A crash outside the logging pipeline would otherwise vanish without a trace when running as a service.
AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject);
TaskScheduler.UnobservedTaskException += (_, e) => { WriteCrash(e.Exception); e.SetObserved(); };

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, BridgeServiceLifetime>(); // sleep/resume and session events
builder.Services.AddHostedService<BridgeWorker>();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddProvider(new FileLoggerProvider(ConfigStore.LogDirectory));
if (WindowsServiceHelpers.IsWindowsService())
    builder.Logging.AddEventLog(new EventLogSettings { SourceName = ServiceName, Filter = (_, l) => l >= LogLevel.Warning });
builder.Logging.SetMinimumLevel(LogLevel.Information);

await builder.Build().RunAsync();
return 0;

static void WriteCrash(object? error)
{
    try
    {
        Directory.CreateDirectory(ConfigStore.LogDirectory);
        File.AppendAllText(Path.Combine(ConfigStore.LogDirectory, "crash.log"),
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}");
    }
    catch { /* nothing left to try */ }
}
