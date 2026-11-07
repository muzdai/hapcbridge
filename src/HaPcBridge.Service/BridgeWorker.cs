using System.Text.Json;
using System.Text.Json.Nodes;
using HaPcBridge.Common;
using HaPcBridge.Service.Agent;
using HaPcBridge.Service.Mqtt;
using HaPcBridge.Service.Power;
using HaPcBridge.Service.Sensors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace HaPcBridge.Service;

internal sealed class BridgeWorker : BackgroundService
{
    readonly ILogger<BridgeWorker> _log;
    readonly MqttClientFactory _factory = new();
    readonly IMqttClient _mqtt;
    readonly AgentHost _agent;
    readonly SemaphoreSlim _publishLock = new(1, 1);

    BridgeConfig _cfg;
    Discovery _disc;
    HardwareMonitor? _hw;
    FileSystemWatcher? _watcher;
    volatile bool _reloadRequested;
    volatile bool _suspending;
    readonly HashSet<string> _publishedConfigTopics = new();
    string _lastStatusText = "";

    public BridgeWorker(ILogger<BridgeWorker> log)
    {
        _log = log;
        _cfg = ConfigStore.LoadAndProtect(out var err);
        if (err != null) _log.LogError("{Err}", err);
        _disc = new Discovery(_cfg);
        _mqtt = _factory.CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += OnMessageAsync;
        _mqtt.DisconnectedAsync += e =>
        {
            if (!_suspending) _log.LogWarning("MQTT disconnected: {Reason}", e.ReasonString ?? e.Exception?.Message ?? e.Reason.ToString());
            return Task.CompletedTask;
        };
        _agent = new AgentHost(log);
        _agent.AgentConnected += PushAgentConfig;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("HA PC Bridge {Version} starting, device '{Id}', config {Path}", Discovery.Version, _cfg.DeviceId, ConfigStore.ConfigPath);
        _hw = new HardwareMonitor(_log);
        WatchConfig();
        _ = Task.Run(() => _agent.RunAsync(ct), ct);

        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_reloadRequested) await ReloadConfigAsync(ct);

                if (!_mqtt.IsConnected && !_suspending)
                {
                    await ConnectAsync(ct);
                    backoff = TimeSpan.FromSeconds(2);
                }

                if (_mqtt.IsConnected) await PublishStateAsync(ct);

                await Task.Delay(TimeSpan.FromSeconds(_cfg.UpdateIntervalSeconds), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("MQTT {Host}:{Port}: {Msg} — retry in {Sec}s", _cfg.Mqtt.Host, _cfg.Mqtt.Port, ex.Message, backoff.TotalSeconds);
                UpdateAgentStatus($"Нет связи с брокером {_cfg.Mqtt.Host}");
                try { await Task.Delay(backoff, ct); } catch { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("Stopping");
        await GoOfflineAsync();
        await base.StopAsync(ct);
        _hw?.Dispose();
        _watcher?.Dispose();
    }

    // ---------------- MQTT ----------------

    async Task ConnectAsync(CancellationToken ct)
    {
        var m = _cfg.Mqtt;
        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(m.Host, m.Port)
            .WithClientId($"hapcbridge-{_cfg.DeviceId}")
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(5, m.KeepAliveSeconds)))
            .WithCleanSession()
            .WithWillTopic(_disc.T.Availability)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTimeout(TimeSpan.FromSeconds(10));

        if (!string.IsNullOrEmpty(m.Username)) b.WithCredentials(m.Username, ConfigStore.GetPassword(m));
        if (m.UseTls)
            b.WithTlsOptions(o =>
            {
                o.UseTls();
                if (m.AllowUntrustedCertificates) o.WithCertificateValidationHandler(_ => true);
            });

        await _mqtt.ConnectAsync(b.Build(), ct);
        _log.LogInformation("Connected to MQTT {Host}:{Port}", m.Host, m.Port);

        var sub = _factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(_disc.T.Command, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(_disc.T.PowerSet, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter($"{_cfg.DiscoveryPrefix}/status", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _mqtt.SubscribeAsync(sub, ct);

        await PublishDiscoveryAsync(ct);
        await PublishInfoAsync(ct);
        await PublishAsync(_disc.T.Availability, "online", retain: true, ct);
        UpdateAgentStatus($"Подключено к {m.Host}");
    }

    async Task GoOfflineAsync()
    {
        if (!_mqtt.IsConnected) return;
        try
        {
            await PublishAsync(_disc.T.Availability, "offline", retain: true, CancellationToken.None);
            await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build());
        }
        catch (Exception ex) { _log.LogDebug("Offline publish failed: {Msg}", ex.Message); }
    }

    Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        return _mqtt.PublishAsync(msg, ct);
    }

    async Task PublishDiscoveryAsync(CancellationToken ct)
    {
        await _publishLock.WaitAsync(ct);
        try
        {
            var wanted = new HashSet<string>();
            foreach (var e in _disc.All())
            {
                var topic = _disc.ConfigTopic(e);
                if (e.Enabled && e.Payload != null)
                {
                    wanted.Add(topic);
                    await PublishAsync(topic, Discovery.Serialize(e.Payload), retain: true, ct);
                }
                else
                {
                    // Disabled in config → remove from Home Assistant.
                    await PublishAsync(topic, "", retain: true, ct);
                }
            }
            foreach (var stale in _publishedConfigTopics.Except(wanted).ToList())
                await PublishAsync(stale, "", retain: true, ct);
            _publishedConfigTopics.Clear();
            _publishedConfigTopics.UnionWith(wanted);
        }
        finally { _publishLock.Release(); }
    }

    async Task PublishInfoAsync(CancellationToken ct)
    {
        var (mac, ip) = SystemInfo.PrimaryNic();
        if (!string.IsNullOrWhiteSpace(_cfg.Wol.Mac)) mac = _cfg.Wol.Mac.Trim().ToUpperInvariant().Replace('-', ':');
        var info = new JsonObject
        {
            ["mac"] = mac,
            ["ip"] = ip,
            ["broadcast"] = _cfg.Wol.BroadcastAddress,
            ["hostname"] = Environment.MachineName,
            ["os"] = Environment.OSVersion.VersionString,
            ["version"] = Discovery.Version,
        };
        await PublishAsync(_disc.T.Info, info.ToJsonString(), retain: true, ct);
    }

    async Task PublishStateAsync(CancellationToken ct)
    {
        var state = BuildState();
        await PublishAsync(_disc.T.State, state.ToJsonString(), retain: false, ct);
    }

    // ---------------- State ----------------

    JsonObject BuildState()
    {
        var s = _cfg.Sensors;
        var o = new JsonObject();
        var hw = _hw?.Read() ?? new HardwareReading();
        var gpu = PrimaryGpu(hw.Gpus);

        if (s.CpuLoad) o["cpu_load"] = R(hw.CpuLoad);
        if (s.CpuTemp) o["cpu_temp"] = R(Temp(hw.CpuTemp));
        if (s.CpuPower) o["cpu_power"] = R(hw.CpuPower);
        if (s.GpuLoad) { o["gpu_load"] = R(gpu?.Load); o["gpu_name"] = gpu?.Name; }
        if (s.GpuTemp) o["gpu_temp"] = R(Temp(gpu?.Temperature));
        if (s.GpuPower) o["gpu_power"] = R(gpu?.Power);
        if (s.GpuMemory) o["gpu_memory_used"] = R(gpu?.MemoryUsedGb, 2);

        if (s.RamFree || s.RamUsage)
        {
            var mem = SystemInfo.ReadMemory();
            if (s.RamFree) o["ram_free"] = R(mem?.FreeGb, 2);
            if (s.RamUsage) o["ram_usage"] = R(mem?.UsagePercent);
        }

        if (s.TotalPower)
        {
            var (watts, source) = TotalPower(hw);
            o["total_power"] = R(watts);
            o["total_power_source"] = source;
        }

        if (s.LoggedUser || s.LastLogin)
        {
            var session = SystemInfo.ConsoleSession();
            if (s.LoggedUser) o["logged_user"] = session?.User ?? "none";
            if (s.LastLogin) o["last_login"] = session?.LogonTime?.ToString("o");
        }
        if (s.LastBoot) o["last_boot"] = SystemInfo.LastBoot.ToString("o");

        var st = AgentStatus();
        if (s.ActiveWindow) o["active_window"] = Trim(st?.ActiveWindow, 250);
        if (s.ActiveProcess) o["active_process"] = st?.ActiveProcess;
        if (s.IdleTime) o["idle_time"] = R(st?.IdleSeconds, 0);
        if (s.Locked) o["locked"] = st?.Locked;

        return o;
    }

    IpcMessage? AgentStatus()
    {
        var st = _agent.LastStatus;
        if (st == null) return null;
        return DateTime.UtcNow - _agent.LastStatusAt > TimeSpan.FromSeconds(Math.Max(30, _cfg.UpdateIntervalSeconds * 3)) ? null : st;
    }

    GpuReading? PrimaryGpu(List<GpuReading> gpus)
    {
        if (gpus.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(_cfg.PrimaryGpu))
        {
            var match = gpus.FirstOrDefault(g => g.Name.Contains(_cfg.PrimaryGpu.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return gpus.FirstOrDefault(g => g.Vendor == "nvidia")
               ?? gpus.OrderByDescending(g => g.MemoryTotalGb ?? 0).ThenByDescending(g => g.Power ?? 0).First();
    }

    /// <summary>
    /// Real wall power from a PSU with telemetry (Corsair HXi/RMi, NZXT …) when present;
    /// otherwise (CPU package + discrete GPUs) / PSU efficiency + baseline for the rest of the system.
    /// </summary>
    (double? Watts, string Source) TotalPower(HardwareReading hw)
    {
        if (hw.PsuPower is > 0) return (hw.PsuPower, $"psu: {hw.PsuName}");

        // An iGPU's power is already part of the CPU package, so only discrete cards are added.
        var gpuW = hw.Gpus.Where(g => g.Vendor == "nvidia" || (g.MemoryTotalGb ?? 0) >= 2).Sum(g => g.Power ?? 0);
        if (hw.CpuPower is null && gpuW == 0) return (null, "unavailable");

        var dc = (hw.CpuPower ?? 0) + gpuW;
        return (dc / _cfg.Power.PsuEfficiency + _cfg.Power.BaselineWatts, "estimate");
    }

    double? Temp(double? c) => c is null ? null : _cfg.IsFahrenheit ? c * 9 / 5 + 32 : c;
    static double? R(double? v, int digits = 1) => v is null || double.IsNaN(v.Value) ? null : Math.Round(v.Value, digits);
    static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    // ---------------- Commands ----------------

    async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString()?.Trim() ?? "";

        if (topic == $"{_cfg.DiscoveryPrefix}/status")
        {
            if (payload == "online")
            {
                _log.LogInformation("Home Assistant restarted — re-sending discovery");
                await Task.Delay(Random.Shared.Next(500, 3000));
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishInfoAsync(CancellationToken.None);
                await PublishAsync(_disc.T.Availability, "online", true, CancellationToken.None);
            }
            return;
        }

        if (topic == _disc.T.PowerSet)
        {
            // "ON" can't reach a powered-off PC; HA does Wake-on-LAN (see README). Ignored here.
            if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase) && _cfg.Commands.PowerSwitch)
                Execute("shutdown");
            return;
        }

        if (topic == _disc.T.Command) Execute(payload.ToLowerInvariant());
    }

    void Execute(string cmd)
    {
        var c = _cfg.Commands;
        bool allowed = cmd switch
        {
            "shutdown" => c.Shutdown || c.PowerSwitch,
            "restart" => c.Restart,
            "abort_shutdown" => c.AbortShutdown,
            "sleep" => c.Sleep,
            "hibernate" => c.Hibernate,
            "lock" => c.Lock,
            "logoff" => c.Logoff,
            "monitor_off" => c.MonitorOff,
            "close_active_window" => c.CloseActiveWindow,
            _ => false,
        };
        if (!allowed) { _log.LogWarning("Command '{Cmd}' is unknown or disabled", cmd); return; }
        _log.LogInformation("Command: {Cmd}", cmd);

        switch (cmd)
        {
            case "shutdown": PowerActions.Shutdown(_log, restart: false, c.ShutdownDelaySeconds); break;
            case "restart": PowerActions.Shutdown(_log, restart: true, c.ShutdownDelaySeconds); break;
            case "abort_shutdown": PowerActions.Abort(_log); break;
            case "sleep": _ = SuspendAsync(false); break;
            case "hibernate": _ = SuspendAsync(true); break;
            case "logoff": PowerActions.Logoff(_log); break;
            case "lock": SendToAgent(AgentCommands.Lock); break;
            case "monitor_off": SendToAgent(AgentCommands.MonitorOff); break;
            case "close_active_window": SendToAgent(AgentCommands.CloseActiveWindow); break;
        }
    }

    void SendToAgent(string cmd)
    {
        if (!_agent.Send(IpcMessage.Cmd(cmd)))
            _log.LogWarning("'{Cmd}' needs the user-session agent, but no agent is connected (nobody logged in?)", cmd);
    }

    /// <summary>Mark offline first, so HA doesn't wait for the keep-alive timeout; reconnect after resume.</summary>
    async Task SuspendAsync(bool hibernate)
    {
        _suspending = true;
        try
        {
            await Task.Delay(500); // let the MQTT ack go out
            await GoOfflineAsync();
            await Task.Run(() => PowerActions.Suspend(_log, hibernate));
        }
        finally
        {
            await Task.Delay(3000); // network usually needs a moment after resume
            _suspending = false;
        }
    }

    // ---------------- Config reload ----------------

    void WatchConfig()
    {
        _watcher = new FileSystemWatcher(ConfigStore.Directory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler h = (_, _) => _reloadRequested = true;
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Renamed += (_, _) => _reloadRequested = true;
    }

    async Task ReloadConfigAsync(CancellationToken ct)
    {
        _reloadRequested = false;
        await Task.Delay(500, ct); // editors write in several steps
        var next = ConfigStore.LoadAndProtect(out var err);
        if (err != null) { _log.LogError("{Err} — keeping the running config", err); return; }

        var oldJson = JsonSerializer.Serialize(_cfg, ConfigStore.JsonOptions);
        if (oldJson == JsonSerializer.Serialize(next, ConfigStore.JsonOptions)) return;
        _log.LogInformation("Config changed — applying");

        bool reconnect = JsonSerializer.Serialize(_cfg.Mqtt) != JsonSerializer.Serialize(next.Mqtt)
                         || _cfg.DeviceId != next.DeviceId || _cfg.BaseTopic != next.BaseTopic
                         || _cfg.DiscoveryPrefix != next.DiscoveryPrefix;

        if (reconnect && _mqtt.IsConnected)
        {
            // Remove this device's entities under the old identity before switching.
            foreach (var t in _publishedConfigTopics) await PublishAsync(t, "", true, ct);
            _publishedConfigTopics.Clear();
            await GoOfflineAsync();
        }

        _cfg = next;
        _disc = new Discovery(_cfg);
        PushAgentConfig();

        if (!reconnect && _mqtt.IsConnected)
        {
            await PublishDiscoveryAsync(ct);
            await PublishInfoAsync(ct);
        }
    }

    // ---------------- Agent ----------------

    void PushAgentConfig() =>
        _agent.Send(new IpcMessage { Type = "config", TrayIcon = _cfg.TrayIcon, StatusText = _lastStatusText });

    void UpdateAgentStatus(string text)
    {
        if (text == _lastStatusText) return;
        _lastStatusText = text;
        PushAgentConfig();
    }
}
