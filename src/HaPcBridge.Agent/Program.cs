using HaPcBridge.Agent;

// One agent per user session.
using var mutex = new Mutex(true, @"Local\HaPcBridge.Agent", out var first);
if (!first) return;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new AgentContext());
