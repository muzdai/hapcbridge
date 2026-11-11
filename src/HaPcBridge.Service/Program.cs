using System.Diagnostics;
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
    case "config":
        ConfigStore.Load(out _);
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ConfigStore.ConfigPath}\"") { UseShellExecute = true });
        return 0;
    case "help" or "-h" or "--help" or "/?":
        Console.WriteLine("""
            HA PC Bridge service
              HaPcBridge.Service.exe install     install and start the Windows service (admin)
              HaPcBridge.Service.exe uninstall   stop and remove the service (admin)
              HaPcBridge.Service.exe config      open config.json in Notepad
              HaPcBridge.Service.exe             run in this console (debugging)
            """);
        return 0;
}

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
