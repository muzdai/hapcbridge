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
    volatile bool _forceReconnect;
    readonly SemaphoreSlim _wake = new(0, 1);
    HardwareReading? _lastHw;
    string? _lastSensorError;
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
        _agent.Request = HandleAgentRequest;
        _agent.StatusChanged += Wake;        // window / lock changed → publish now, not at the next tick

        HostEvents.SessionChanged += Wake;   // logon, logoff, RDP
        HostEvents.Suspending += OnSuspending;
        HostEvents.Resumed += OnResumed;
    }

    /// <summary>Interrupts the wait between updates: settings changed, something happened, or we resumed.</summary>
    void Wake()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Windows is going to sleep (any reason): tell HA now instead of after the keep-alive timeout.</summary>
    void OnSuspending()
    {
        _suspending = true;
        _log.LogInformation("System is suspending");
        try { GoOfflineAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    void OnResumed()
    {
        if (!_suspending && !_forceReconnect) _log.LogInformation("System resumed");
        _suspending = false;
        _forceReconnect = true; // the old TCP connection is dead even if the client still thinks it's open
        Wake();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("HA PC Bridge {Version} starting, device '{Id}', config {Path}", Discovery.Version, _cfg.DeviceId, ConfigStore.ConfigPath);
        _hw = new HardwareMonitor(_log);
        WatchConfig();
        _ = Task.Run(() => _agent.RunAsync(ct), ct);

        var backoff = TimeSpan.FromSeconds(2);
        var nextFull = DateTime.MinValue; // when the hardware sensors are read again
        var woken = false;                // the last wait ended early because of an event
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                if (_reloadRequested) { await ReloadConfigAsync(ct); nextFull = DateTime.MinValue; }

                if (_forceReconnect)
                {
                    _forceReconnect = false;
                    await DropConnectionAsync();
                }

                if (!_mqtt.IsConnected && !_suspending)
                {
                    await ConnectAsync(ct);
                    backoff = TimeSpan.FromSeconds(2);
                    nextFull = DateTime.MinValue;
                }

                if (_mqtt.IsConnected)
                {
                    // Woken early by an event → cheap publish with the cached hardware reading.
                    // (A wait that simply timed out is always a full update, even if the timer fired a few ms early.)
                    var full = !woken || DateTime.UtcNow >= nextFull;
                    await PublishStateAsync(full, ct);
                    if (full) nextFull = DateTime.UtcNow.AddSeconds(_cfg.UpdateIntervalSeconds);
                    wait = nextFull - DateTime.UtcNow;
                    if (wait < TimeSpan.FromMilliseconds(200)) wait = TimeSpan.FromMilliseconds(200);
                }
                else wait = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (!_mqtt.IsConnected || ex is MQTTnet.Exceptions.MqttCommunicationException)
                {
                    _log.LogWarning("MQTT {Host}:{Port}: {Msg} - retry in {Sec}s", _cfg.Mqtt.Host, _cfg.Mqtt.Port, ex.Message, backoff.TotalSeconds);
                    UpdateAgentStatus($"Нет связи с брокером {_cfg.Mqtt.Host}");
                }
                else _log.LogError(ex, "Update failed (not a broker problem) - retry in {Sec}s", backoff.TotalSeconds);
                wait = backoff;
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }

            try
            {
                // Returns early when Wake() is called; a short pause then coalesces bursts (alt-tab, editors saving twice).
                woken = await _wake.WaitAsync(wait, ct);
                if (woken) await Task.Delay(150, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("Stopping");
        await GoOfflineAsync();
        await base.StopAsync(ct);
        HostEvents.SessionChanged -= Wake;
        HostEvents.Suspending -= OnSuspending;
        HostEvents.Resumed -= OnResumed;
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
            .WithTopicFilter(_disc.T.ConfigSet, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(_disc.T.Notify, MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter($"{_cfg.DiscoveryPrefix}/status", MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _mqtt.SubscribeAsync(sub, ct);

        await PublishDiscoveryAsync(ct);
        await PublishInfoAsync(ct);
        await PublishConfigAsync(ct);
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

    /// <summary>Closes a connection that is probably dead (after resume) without waiting on the broker.</summary>
    async Task DropConnectionAsync()
    {
        if (!_mqtt.IsConnected) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _mqtt.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cts.Token);
        }
        catch { /* dead socket — the client is disconnected either way */ }
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
                if (_cfg.HaDiscovery && e.Enabled && e.Payload != null)
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
        (string? mac, string? ip) = (null, null);
        try { (mac, ip) = SystemInfo.PrimaryNic(); }
        catch (Exception ex) { _log.LogWarning("Network adapters: {Msg}", ex.Message); }
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

    Task PublishConfigAsync(CancellationToken ct) =>
        PublishAsync(_disc.T.ConfigState, RemoteConfig.Public(_cfg).ToJsonString(), retain: true, ct);

    async Task PublishStateAsync(bool refreshHardware, CancellationToken ct)
    {
        var state = BuildState(refreshHardware);
        await PublishAsync(_disc.T.State, state.ToJsonString(), retain: false, ct);
    }

    // ---------------- State ----------------

    /// <summary>Reads the hardware sensors; a failing sensor library must not look like an MQTT problem.</summary>
    HardwareReading ReadHardware()
    {
        try
        {
            var r = _hw?.Read() ?? new HardwareReading();
            if (_lastSensorError != null) { _log.LogInformation("Hardware sensors are readable again"); _lastSensorError = null; }
            return r;
        }
        catch (Exception ex)
        {
            if (ex.Message != _lastSensorError) { _lastSensorError = ex.Message; _log.LogWarning("Hardware sensors: {Msg}", ex.Message); }
            return new HardwareReading();
        }
    }

    JsonObject BuildState(bool refreshHardware = true)
    {
        var s = _cfg.Sensors;
        var o = new JsonObject();
        if (refreshHardware || _lastHw == null) _lastHw = ReadHardware();
        var hw = _lastHw;
        var gpu = Estimator.PrimaryGpu(hw.Gpus, _cfg.PrimaryGpu);

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
            var (watts, source) = Estimator.TotalPower(hw, _cfg.Power);
            o["total_power"] = R(watts);
            o["total_power_source"] = source;
        }

        if (s.LoggedUser || s.LastLogin)
        {
            var session = SystemInfo.UserSession();
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

    double? Temp(double? c) => Estimator.ToDisplayTemperature(c, _cfg.IsFahrenheit);
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
                _log.LogInformation("Home Assistant restarted - re-sending discovery");
                await Task.Delay(Random.Shared.Next(500, 3000));
                await PublishDiscoveryAsync(CancellationToken.None);
                await PublishInfoAsync(CancellationToken.None);
                await PublishAsync(_disc.T.Availability, "online", true, CancellationToken.None);
            }
            return;
        }

        // A retained command would run again on every reconnect (e.g. shut the PC down at each boot).
        if (e.ApplicationMessage.Retain)
        {
            _log.LogWarning("Ignored retained message on {Topic}; clear it on the broker", topic);
            return;
        }

        if (topic == _disc.T.PowerSet)
        {
            // "ON" can't reach a powered-off PC; HA does Wake-on-LAN (see README). Ignored here.
            if (payload.Equals("OFF", StringComparison.OrdinalIgnoreCase) && _cfg.Commands.PowerSwitch)
                Execute("shutdown");
            return;
        }

        if (topic == _disc.T.ConfigSet) { ApplyRemoteConfig(payload); return; }
        if (topic == _disc.T.Notify) { ShowNotification(payload); return; }

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

    void ShowNotification(string payload)
    {
        if (payload.Length == 0) return; // someone clearing the topic
        if (!_cfg.Commands.Notifications) { _log.LogWarning("Notification ignored: notifications are disabled in the settings"); return; }
        var n = Notification.Parse(payload);
        if (n == null) { _log.LogWarning("Notification ignored: expected text or JSON with \"message\""); return; }
        if (!_agent.Send(new IpcMessage { Type = "notify", Payload = n.ToJson() }))
            _log.LogWarning("Notification not shown: nobody is logged in");
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
            await Task.Run(() => PowerActions.Suspend(_log, hibernate)); // returns after resume
        }
        finally
        {
            await Task.Delay(3000); // network usually needs a moment after resume
            OnResumed();
        }
    }

    // ---------------- Remote config (from the HA integration / card) ----------------

    void ApplyRemoteConfig(string payload)
    {
        if (!_cfg.AllowRemoteConfig) { _log.LogWarning("Remote config change ignored: allowRemoteConfig is off"); return; }
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject patch) { _log.LogWarning("config/set: expected a JSON object"); return; }
            var rejected = new List<string>();
            var next = RemoteConfig.Apply(_cfg, patch, rejected);
            if (rejected.Count > 0) _log.LogWarning("config/set: ignored keys {Keys}", string.Join(", ", rejected));
            ConfigStore.Save(next); // the file watcher applies it like a local edit
            _log.LogInformation("Settings changed from Home Assistant");
        }
        catch (Exception ex) { _log.LogWarning("config/set rejected: {Msg}", ex.Message); }
    }

    // ---------------- Settings window in the agent ----------------

    /// <summary>
    /// The agent runs as the logged-on user and can't write %ProgramData%\HaPcBridge, so it edits
    /// the config through us. Only our own agent in the console session can reach this pipe.
    /// </summary>
    IpcMessage? HandleAgentRequest(IpcMessage msg)
    {
        switch (msg.Type)
        {
            case "notify_event":
                // The user clicked a notification or one of its buttons → tell Home Assistant.
                if (_mqtt.IsConnected && !string.IsNullOrEmpty(msg.Payload))
                    _ = PublishAsync(_disc.T.NotifyEvent, msg.Payload, retain: false, CancellationToken.None)
                        .ContinueWith(t => _log.LogDebug("notify event not sent: {Msg}", t.Exception?.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);
                return null;
            case "get_config":
            {
                var copy = JsonSerializer.Deserialize<BridgeConfig>(JsonSerializer.Serialize(_cfg, ConfigStore.JsonOptions), ConfigStore.JsonOptions)!;
                var hasPassword = !string.IsNullOrEmpty(ConfigStore.GetPassword(copy.Mqtt));
                copy.Mqtt.Password = null;
                copy.Mqtt.PasswordProtected = null;
                return new IpcMessage
                {
                    Type = "full_config",
                    ConfigJson = JsonSerializer.Serialize(copy, ConfigStore.JsonOptions),
                    HasPassword = hasPassword,
                    StatusText = _lastStatusText,
                };
            }
            case "set_config":
            {
                var next = JsonSerializer.Deserialize<BridgeConfig>(msg.ConfigJson ?? "", ConfigStore.JsonOptions)
                           ?? throw new InvalidOperationException("empty config");
                // Keep the stored password unless a new one was typed; a new one is encrypted on reload.
                next.Mqtt.PasswordProtected = _cfg.Mqtt.PasswordProtected;
                next.Mqtt.Password = string.IsNullOrEmpty(msg.Password) ? _cfg.Mqtt.Password : msg.Password;
                if (string.IsNullOrWhiteSpace(next.Mqtt.Host)) throw new InvalidOperationException("MQTT host is empty");
                ConfigStore.Normalize(next);
                ConfigStore.Save(next); // the file watcher applies it
                _log.LogInformation("Settings changed from the tray settings window");
                return new IpcMessage { Type = "saved", Ok = true };
            }
        }
        return null;
    }

    // ---------------- Config reload ----------------

    void WatchConfig()
    {
        _watcher = new FileSystemWatcher(ConfigStore.Directory, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler h = (_, _) => { _reloadRequested = true; Wake(); };
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Renamed += (_, _) => { _reloadRequested = true; Wake(); };
    }

    async Task ReloadConfigAsync(CancellationToken ct)
    {
        _reloadRequested = false;
        await Task.Delay(500, ct); // editors write in several steps
        var next = ConfigStore.LoadAndProtect(out var err);
        if (err != null) { _log.LogError("{Err} - keeping the running config", err); return; }

        var oldJson = JsonSerializer.Serialize(_cfg, ConfigStore.JsonOptions);
        if (oldJson == JsonSerializer.Serialize(next, ConfigStore.JsonOptions)) return;
        _log.LogInformation("Config changed - applying");

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
            await PublishConfigAsync(ct);
            // the main loop publishes the state right after this
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
