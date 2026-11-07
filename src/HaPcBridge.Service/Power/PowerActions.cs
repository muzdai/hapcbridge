using System.Diagnostics;
using HaPcBridge.Service.Native;
using Microsoft.Extensions.Logging;

namespace HaPcBridge.Service.Power;

internal static class PowerActions
{
    const string Reason = "Home Assistant (HA PC Bridge)";

    /// <summary>Shutdown or restart with a visible Windows warning; can be cancelled with <see cref="Abort"/> or "shutdown /a".</summary>
    public static void Shutdown(ILogger log, bool restart, int delaySeconds)
    {
        var args = $"{(restart ? "/r" : "/s")} /t {delaySeconds} /d p:0:0 /c \"{Reason}\"";
        if (delaySeconds == 0) args += " /f";
        Run(log, "shutdown.exe", args);
    }

    public static void Abort(ILogger log) => Run(log, "shutdown.exe", "/a");

    public static void Suspend(ILogger log, bool hibernate)
    {
        log.LogInformation(hibernate ? "Hibernating" : "Sleeping");
        if (!Win32.SetSuspendState(hibernate, false, false))
            log.LogWarning("SetSuspendState failed: {Err}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    public static void Logoff(ILogger log)
    {
        var s = Win32.WTSGetActiveConsoleSessionId();
        if (s == Win32.INVALID_SESSION || s == 0) return;
        if (!Win32.WTSLogoffSession(Win32.WTS_CURRENT_SERVER_HANDLE, s, false))
            log.LogWarning("WTSLogoffSession failed: {Err}", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    static void Run(ILogger log, string exe, string args)
    {
        log.LogInformation("Running {Exe} {Args}", exe, args);
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { log.LogError(ex, "{Exe} failed", exe); }
    }
}
