using System.Net.NetworkInformation;
using System.Net.Sockets;
using HaPcBridge.Service.Native;

namespace HaPcBridge.Service.Sensors;

internal static class SystemInfo
{
    public sealed record Memory(double TotalGb, double FreeGb, double UsagePercent);

    public static Memory? ReadMemory()
    {
        var m = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref m)) return null;
        const double gb = 1024.0 * 1024 * 1024;
        return new Memory(m.ullTotalPhys / gb, m.ullAvailPhys / gb, m.dwMemoryLoad);
    }

    public static DateTimeOffset LastBoot =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public sealed record Session(uint Id, string? User, DateTimeOffset? LogonTime);

    /// <summary>The user sitting at the physical console (not RDP), or null when nobody is logged in.</summary>
    public static Session? ConsoleSession()
    {
        var id = Win32.WTSGetActiveConsoleSessionId();
        if (id == Win32.INVALID_SESSION || id == 0) return null;

        var user = Win32.QueryString(id, Win32.WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user)) return new Session(id, null, null);

        var domain = Win32.QueryString(id, Win32.WTS_INFO_CLASS.WTSDomainName);
        var info = Win32.QuerySessionInfo(id);
        DateTimeOffset? logon = info is { LogonTime: > 0 } i ? DateTimeOffset.FromFileTime(i.LogonTime).ToUniversalTime() : null;

        // Show "DOMAIN\user" only for real domains; a local account shows just the name.
        var display = !string.IsNullOrEmpty(domain) && !domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? $"{domain}\\{user}" : user;
        return new Session(id, display, logon);
    }

    /// <summary>MAC of the NIC that carries the IPv4 default route — the one WoL should target.</summary>
    public static (string? Mac, string? Ip) PrimaryNic()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (Nic: n, Props: n.GetIPProperties()))
            .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(x => x.Nic.Speed)
            .ToList();
        if (nics.Count == 0) return (null, null);

        var (nic, props) = nics[0];
        var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        var ip = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
        return (mac.Length > 0 ? mac : null, ip);
    }
}
