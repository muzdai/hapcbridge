using System.Text.Json;
using System.Text.Json.Nodes;
using HaPcBridge.Common;

namespace HaPcBridge.Service.Mqtt;

/// <summary>Home Assistant MQTT discovery payloads for every entity this bridge can expose.</summary>
internal sealed class Discovery
{
    readonly BridgeConfig _cfg;
    public Topics T { get; }

    public Discovery(BridgeConfig cfg)
    {
        _cfg = cfg;
        T = new Topics(cfg);
    }

    public sealed record Entity(string Component, string Key, bool Enabled, JsonObject? Payload);

    public IEnumerable<Entity> All()
    {
        var s = _cfg.Sensors;
        var c = _cfg.Commands;
        var tUnit = _cfg.IsFahrenheit ? "°F" : "°C";

        // --- power switch: state = availability, so HA shows "off" also after a crash (LWT) ---
        yield return new("switch", "power", c.PowerSwitch, c.PowerSwitch ? Build("switch", "power", "Power", icon: "mdi:desktop-tower-monitor",
            extra: new()
            {
                ["command_topic"] = T.PowerSet,
                ["state_topic"] = T.Availability,
                ["payload_on"] = "ON",
                ["payload_off"] = "OFF",
                ["state_on"] = "online",
                ["state_off"] = "offline",
            }, withAvailability: false) : null);

        // --- sensors ---
        yield return Sensor("cpu_load", "CPU load", s.CpuLoad, "%", null, "measurement", "mdi:cpu-64-bit", precision: 0);
        yield return Sensor("cpu_temp", "CPU temperature", s.CpuTemp, tUnit, "temperature", "measurement", null, precision: 0);
        yield return Sensor("cpu_power", "CPU power", s.CpuPower, "W", "power", "measurement", null, precision: 0);
        yield return Sensor("gpu_load", "GPU load", s.GpuLoad, "%", null, "measurement", "mdi:expansion-card", precision: 0, attrs: "gpu_name");
        yield return Sensor("gpu_temp", "GPU temperature", s.GpuTemp, tUnit, "temperature", "measurement", null, precision: 0);
        yield return Sensor("gpu_power", "GPU power", s.GpuPower, "W", "power", "measurement", null, precision: 0);
        yield return Sensor("gpu_memory_used", "GPU memory used", s.GpuMemory, "GB", "data_size", "measurement", "mdi:memory", precision: 1);
        yield return Sensor("ram_free", "RAM free", s.RamFree, "GB", "data_size", "measurement", "mdi:memory", precision: 1);
        yield return Sensor("ram_usage", "RAM usage", s.RamUsage, "%", null, "measurement", "mdi:memory", precision: 0);
        yield return Sensor("total_power", "Total power", s.TotalPower, "W", "power", "measurement", "mdi:flash", precision: 0, attrs: "total_power_source");
        yield return Sensor("logged_user", "Logged user", s.LoggedUser, null, null, null, "mdi:account");
        yield return Sensor("last_login", "Last login", s.LastLogin, null, "timestamp", null, "mdi:login");
        yield return Sensor("last_boot", "Last boot", s.LastBoot, null, "timestamp", null, "mdi:restart", diagnostic: true);
        yield return Sensor("active_window", "Active window", s.ActiveWindow, null, null, null, "mdi:application-outline");
        yield return Sensor("active_process", "Active process", s.ActiveProcess, null, null, null, "mdi:application-cog-outline");
        yield return Sensor("idle_time", "Idle time", s.IdleTime, "s", "duration", "measurement", "mdi:timer-sand", precision: 0);

        yield return new("binary_sensor", "locked", s.Locked, s.Locked ? Build("binary_sensor", "locked", "Locked", icon: "mdi:lock",
            extra: new()
            {
                ["state_topic"] = T.State,
                ["value_template"] = "{{ 'ON' if value_json.locked else 'OFF' }}",
            }) : null);

        // MAC for Wake-on-LAN: no availability so it stays readable while the PC is off.
        yield return new("sensor", "mac", true, Build("sensor", "mac", "MAC address", icon: "mdi:lan", diagnostic: true,
            extra: new()
            {
                ["state_topic"] = T.Info,
                ["value_template"] = "{{ value_json.mac }}",
                ["json_attributes_topic"] = T.Info,
            }, withAvailability: false));

        // --- buttons ---
        yield return Button("shutdown", "Shutdown", c.Shutdown, "mdi:power");
        yield return Button("restart", "Restart", c.Restart, "mdi:restart");
        yield return Button("abort_shutdown", "Abort shutdown", c.AbortShutdown, "mdi:cancel");
        yield return Button("sleep", "Sleep", c.Sleep, "mdi:power-sleep");
        yield return Button("hibernate", "Hibernate", c.Hibernate, "mdi:snowflake");
        yield return Button("lock", "Lock", c.Lock, "mdi:lock");
        yield return Button("logoff", "Log off", c.Logoff, "mdi:logout");
        yield return Button("monitor_off", "Monitor off", c.MonitorOff, "mdi:monitor-off");
        yield return Button("close_active_window", "Close active window", c.CloseActiveWindow, "mdi:close-box-outline");
    }

    Entity Sensor(string key, string name, bool enabled, string? unit, string? deviceClass, string? stateClass, string? icon,
        int? precision = null, string? attrs = null, bool diagnostic = false)
    {
        if (!enabled) return new("sensor", key, false, null);
        var extra = new JsonObject
        {
            ["state_topic"] = T.State,
            ["value_template"] = $"{{{{ value_json.{key} }}}}",
        };
        if (unit != null) extra["unit_of_measurement"] = unit;
        if (deviceClass != null) extra["device_class"] = deviceClass;
        if (stateClass != null) extra["state_class"] = stateClass;
        if (precision != null) extra["suggested_display_precision"] = precision;
        if (attrs != null)
        {
            extra["json_attributes_topic"] = T.State;
            extra["json_attributes_template"] = $"{{{{ {{'{attrs}': value_json.{attrs}}} | tojson }}}}";
        }
        return new("sensor", key, true, Build("sensor", key, name, icon, extra, diagnostic: diagnostic));
    }

    Entity Button(string key, string name, bool enabled, string icon)
    {
        if (!enabled) return new("button", key, false, null);
        return new("button", key, true, Build("button", key, name, icon, new()
        {
            ["command_topic"] = T.Command,
            ["payload_press"] = key,
        }));
    }

    JsonObject Build(string component, string key, string name, string? icon, JsonObject extra, bool withAvailability = true, bool diagnostic = false)
    {
        var o = new JsonObject
        {
            ["name"] = name,
            ["unique_id"] = $"hapcbridge_{_cfg.DeviceId}_{key}",
            ["default_entity_id"] = $"{component}.{_cfg.DeviceId}_{key}",
            ["device"] = DeviceBlock(),
            ["origin"] = new JsonObject
            {
                ["name"] = "HA PC Bridge",
                ["sw_version"] = Version,
                ["support_url"] = "https://github.com/muzdai/hapcbridge",
            },
        };
        if (icon != null) o["icon"] = icon;
        if (diagnostic) o["entity_category"] = "diagnostic";
        if (withAvailability)
        {
            o["availability_topic"] = T.Availability;
            o["payload_available"] = "online";
            o["payload_not_available"] = "offline";
        }
        foreach (var kv in extra) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    JsonObject DeviceBlock() => new()
    {
        ["identifiers"] = new JsonArray($"hapcbridge_{_cfg.DeviceId}"),
        ["name"] = _cfg.DeviceName,
        ["manufacturer"] = "HA PC Bridge",
        ["model"] = "Windows PC",
        ["sw_version"] = Version,
    };

    public string ConfigTopic(Entity e) => $"{_cfg.DiscoveryPrefix}/{e.Component}/hapcbridge_{_cfg.DeviceId}/{e.Key}/config";

    public static string Serialize(JsonObject o) => o.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    public static readonly string Version =
        typeof(Discovery).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

internal sealed class Topics
{
    public Topics(BridgeConfig cfg)
    {
        Root = $"{cfg.BaseTopic}/{cfg.DeviceId}";
    }

    public string Root { get; }
    public string Availability => $"{Root}/availability";
    public string State => $"{Root}/state";
    public string Info => $"{Root}/info";
    public string Command => $"{Root}/command";
    public string PowerSet => $"{Root}/power/set";
    public string ConfigSet => $"{Root}/config/set";
    public string ConfigState => $"{Root}/config";
}
