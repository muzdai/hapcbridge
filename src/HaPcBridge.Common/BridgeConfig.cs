using System.Text.Json.Serialization;

namespace HaPcBridge.Common;

/// <summary>Root of %ProgramData%\HaPcBridge\config.json.</summary>
public sealed class BridgeConfig
{
    public MqttConfig Mqtt { get; set; } = new();

    /// <summary>Unique id of this PC on the broker (topic segment). Default: machine name.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Device name shown in Home Assistant. Default: machine name.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>MQTT topic prefix for state/commands.</summary>
    public string BaseTopic { get; set; } = "hapcbridge";

    /// <summary>Home Assistant discovery prefix.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>How often sensors are read and published.</summary>
    public int UpdateIntervalSeconds { get; set; } = 10;

    /// <summary>"C" or "F".</summary>
    public string TemperatureUnit { get; set; } = "C";

    /// <summary>
    /// Part of the GPU name to report as "the" GPU (e.g. "4080", "7900"). Empty = first NVIDIA card,
    /// otherwise the card with the most VRAM (so a Ryzen iGPU never wins over a discrete card).
    /// </summary>
    public string PrimaryGpu { get; set; } = "";

    /// <summary>
    /// Publish Home Assistant MQTT discovery. The HACS integration switches this off
    /// when it adopts the PC, so entities aren't created twice.
    /// </summary>
    public bool HaDiscovery { get; set; } = true;

    /// <summary>Accept settings changes from Home Assistant (topic …/config/set). Broker settings are never changed remotely.</summary>
    public bool AllowRemoteConfig { get; set; } = true;

    /// <summary>Show the tray icon in the user session.</summary>
    public bool TrayIcon { get; set; } = true;

    public SensorToggles Sensors { get; set; } = new();
    public CommandToggles Commands { get; set; } = new();
    public PowerConfig Power { get; set; } = new();
    public WolConfig Wol { get; set; } = new();

    [JsonIgnore]
    public bool IsFahrenheit => TemperatureUnit.Trim().StartsWith("F", StringComparison.OrdinalIgnoreCase);
}

public sealed class MqttConfig
{
    public string Host { get; set; } = "192.168.1.253";
    public int Port { get; set; } = 1883;
    public string Username { get; set; } = "";

    /// <summary>
    /// Plain password. On first start the service encrypts it (DPAPI, machine scope)
    /// into <see cref="PasswordProtected"/> and clears this field.
    /// </summary>
    public string? Password { get; set; }

    public string? PasswordProtected { get; set; }

    public bool UseTls { get; set; }

    /// <summary>Accept self-signed broker certificates when TLS is on.</summary>
    public bool AllowUntrustedCertificates { get; set; }

    public int KeepAliveSeconds { get; set; } = 30;
}

/// <summary>Each sensor can be switched off; disabled sensors are removed from Home Assistant.</summary>
public sealed class SensorToggles
{
    public bool CpuLoad { get; set; } = true;
    public bool CpuTemp { get; set; } = true;
    public bool CpuPower { get; set; } = true;
    public bool GpuLoad { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool GpuPower { get; set; } = true;
    public bool GpuMemory { get; set; } = true;
    public bool RamFree { get; set; } = true;
    public bool RamUsage { get; set; } = true;
    public bool TotalPower { get; set; } = true;
    public bool LoggedUser { get; set; } = true;
    public bool LastLogin { get; set; } = true;
    public bool LastBoot { get; set; } = true;
    public bool ActiveWindow { get; set; } = true;
    public bool ActiveProcess { get; set; } = true;
    public bool IdleTime { get; set; } = true;
    public bool Locked { get; set; } = true;
}

public sealed class CommandToggles
{
    /// <summary>The on/off power switch (off = shutdown, on = request WoL from HA).</summary>
    public bool PowerSwitch { get; set; } = true;
    public bool Shutdown { get; set; } = true;
    public bool Restart { get; set; } = true;
    public bool Sleep { get; set; } = true;
    public bool Hibernate { get; set; } = false;
    public bool Lock { get; set; } = true;
    public bool Logoff { get; set; } = true;
    public bool MonitorOff { get; set; } = true;
    public bool CloseActiveWindow { get; set; } = true;
    public bool AbortShutdown { get; set; } = true;

    /// <summary>Seconds of warning before shutdown/restart (0 = immediately). Can be aborted.</summary>
    public int ShutdownDelaySeconds { get; set; } = 30;
}

public sealed class PowerConfig
{
    /// <summary>
    /// Watts added to CPU+GPU for everything the sensors can't see
    /// (motherboard, RAM, drives, fans, NIC, PSU losses). Ignored when a PSU with telemetry is found.
    /// </summary>
    public double BaselineWatts { get; set; } = 60;

    /// <summary>PSU efficiency used to convert DC load to wall power in estimate mode (0.85–0.94 typical).</summary>
    public double PsuEfficiency { get; set; } = 0.90;
}

public sealed class WolConfig
{
    /// <summary>MAC to publish for Wake-on-LAN. Empty = autodetect the NIC with the default route.</summary>
    public string Mac { get; set; } = "";

    /// <summary>Broadcast address HA should send the magic packet to.</summary>
    public string BroadcastAddress { get; set; } = "255.255.255.255";
}
