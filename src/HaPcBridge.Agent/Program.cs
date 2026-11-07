using HaPcBridge.Agent;

// One agent per user session. A second start with --settings asks the running one to open its window.
using var mutex = new Mutex(true, @"Local\HaPcBridge.Agent", out var first);
if (!first)
{
    if (args.Contains("--settings") && EventWaitHandle.TryOpenExisting(Program.OpenSettingsEvent, out var ev))
        ev.Set();
    return;
}

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new AgentContext());

internal static partial class Program
{
    public const string OpenSettingsEvent = @"Local\HaPcBridge.OpenSettings";
}
