using System.Diagnostics;
using System.Security.Principal;
using HaPcBridge.Common;

namespace HaPcBridge.Service;

internal static class Installer
{
    public static int Install(string name)
    {
        if (!IsAdmin()) { Console.Error.WriteLine("Run as Administrator."); return 1; }
        var exe = Environment.ProcessPath!;

        var binPath = $"binPath= \"\\\"{exe}\\\"\" start= delayed-auto DisplayName= \"HomeAssistant PC Bridge\"";
        if (Sc($"query {name}", quiet: true) == 0)
        {
            // Already registered (update, repair, or an older copy elsewhere): re-point it instead of
            // delete + create, which fails while services.msc or anything else holds the service open.
            Sc($"stop {name}", quiet: true);
            WaitStopped(name);
            if (Sc($"config {name} {binPath}", quiet: true) != 0)
            {
                // Couldn't re-point it: fall back to removing and creating it again.
                Sc($"delete {name}", quiet: true);
                Thread.Sleep(1500);
                if (Sc($"create {name} {binPath}") != 0) return 1;
            }
        }
        else if (Sc($"create {name} {binPath}") != 0) return 1;

        Sc($"description {name} \"Publishes PC state to Home Assistant over MQTT and accepts power/window commands.\"");
        // Restart after 5 s on the first three failures, reset the counter daily.
        Sc($"failure {name} reset= 86400 actions= restart/5000/restart/5000/restart/30000");

        ConfigStore.Load(out _); // creates config.json with defaults if missing
        // The folder holds the (DPAPI-encrypted) MQTT password: SYSTEM and Administrators only.
        Run("icacls.exe", $"\"{ConfigStore.Directory}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /T /Q");
        Console.WriteLine($"Config: {ConfigStore.ConfigPath}");
        Console.WriteLine("Edit the MQTT host/user/password there; the service picks changes up automatically.");

        return Sc($"start {name}");
    }

    public static int Uninstall(string name)
    {
        if (!IsAdmin()) { Console.Error.WriteLine("Run as Administrator."); return 1; }
        Sc($"stop {name}", quiet: true);
        WaitStopped(name);
        foreach (var p in Process.GetProcessesByName("HaPcBridge.Agent")) { try { p.Kill(); } catch { } }
        return Sc($"delete {name}");
    }

    /// <summary>Waits up to 20 s for the service process to exit, so its files can be replaced.</summary>
    static void WaitStopped(string name)
    {
        for (var i = 0; i < 40; i++)
        {
            var psi = new ProcessStartInfo("sc.exe", $"query {name}") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(500);
        }
    }

    static int Sc(string args, bool quiet = false) => Run("sc.exe", args, quiet);

    static int Run(string exe, string args, bool quiet = true)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (!quiet) Console.WriteLine(output.Trim());
        return p.ExitCode;
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
