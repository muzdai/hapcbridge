using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPcBridge.Service;

/// <summary>Windows power and session notifications, forwarded from the service control handler.</summary>
internal static class HostEvents
{
    /// <summary>The PC is about to sleep or hibernate. Handlers run synchronously and have about two seconds.</summary>
    public static event Action? Suspending;
    public static event Action? Resumed;
    /// <summary>Logon, logoff, lock, unlock, RDP connect or disconnect.</summary>
    public static event Action? SessionChanged;

    public static void RaiseSuspending() => Safe(Suspending);
    public static void RaiseResumed() => Safe(Resumed);
    public static void RaiseSessionChanged() => Safe(SessionChanged);

    static void Safe(Action? a)
    {
        try { a?.Invoke(); } catch { /* never break the service control handler */ }
    }
}

/// <summary>Service lifetime that also listens for sleep/resume and session changes.</summary>
internal sealed class BridgeServiceLifetime : WindowsServiceLifetime
{
    public BridgeServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor, IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                HostEvents.RaiseSuspending();
                break;
            case PowerBroadcastStatus.ResumeSuspend:
            case PowerBroadcastStatus.ResumeAutomatic:
            case PowerBroadcastStatus.ResumeCritical:
                HostEvents.RaiseResumed();
                break;
        }
        return base.OnPowerEvent(powerStatus);
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        HostEvents.RaiseSessionChanged();
        base.OnSessionChange(changeDescription);
    }
}
