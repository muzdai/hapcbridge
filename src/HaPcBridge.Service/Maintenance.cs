using HaPcBridge.Common;
using HaPcBridge.Service.Mqtt;
using MQTTnet;
using MQTTnet.Protocol;

namespace HaPcBridge.Service;

/// <summary>One-shot commands used by the installer.</summary>
internal static class Maintenance
{
    /// <summary>What the installer steps did: %ProgramData%\HaPcBridge\logs\setup.log (never contains the password).</summary>
    public static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.LogDirectory);
            File.AppendAllText(Path.Combine(ConfigStore.LogDirectory, "setup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { /* diagnostics only */ }
    }

    /// <summary>
    /// Writes the broker settings entered in the installer.
    /// "--from FILE": a text file with host, port, user and password on four lines, deleted after reading
    /// (what the installer uses). Otherwise --host/--port/--user arguments or HAPCBRIDGE_MQTT_* variables.
    /// </summary>
    public static int Init(string[] args)
    {
        string? fileHost = null, filePort = null, fileUser = null, filePassword = null;
        var from = Array.FindIndex(args, a => a.Equals("--from", StringComparison.OrdinalIgnoreCase));
        if (from >= 0 && from + 1 < args.Length)
        {
            var path = args[from + 1];
            var lines = File.ReadAllLines(path);
            try { File.Delete(path); } catch { /* the installer removes its temp folder anyway */ }
            fileHost = lines.ElementAtOrDefault(0);
            filePort = lines.ElementAtOrDefault(1);
            fileUser = lines.ElementAtOrDefault(2);
            filePassword = lines.ElementAtOrDefault(3);
        }

        string? Arg(string name, string? fromFile)
        {
            if (fromFile != null) return fromFile;
            var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            return Environment.GetEnvironmentVariable("HAPCBRIDGE_MQTT_" + name.ToUpperInvariant());
        }

        var cfg = ConfigStore.Load(out var error);
        if (error != null) { Console.Error.WriteLine(error); cfg = new BridgeConfig(); ConfigStore.Normalize(cfg); }

        if (Arg("host", fileHost) is { Length: > 0 } host) cfg.Mqtt.Host = host.Trim();
        if (int.TryParse(Arg("port", filePort), out var port) && port is > 0 and < 65536) cfg.Mqtt.Port = port;
        if (Arg("user", fileUser) is { } user) cfg.Mqtt.Username = user.Trim();
        var password = filePassword ?? Environment.GetEnvironmentVariable("HAPCBRIDGE_MQTT_PASSWORD");
        if (!string.IsNullOrEmpty(password)) { cfg.Mqtt.Password = password; cfg.Mqtt.PasswordProtected = null; }

        ConfigStore.Save(cfg);
        ConfigStore.LoadAndProtect(out _); // encrypt the password right away

        // Read it back: the installer must not report success for settings that are not on disk.
        var saved = ConfigStore.Load(out var readError);
        var ok = readError == null && saved.Mqtt.Host == cfg.Mqtt.Host && saved.Mqtt.Username == cfg.Mqtt.Username
                 && (string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(saved.Mqtt.PasswordProtected));
        Log($"init: broker {cfg.Mqtt.Host}:{cfg.Mqtt.Port}, user {(cfg.Mqtt.Username.Length > 0 ? "set" : "empty")}, " +
            $"password {(string.IsNullOrEmpty(password) ? "not given" : "given")}, source {(from >= 0 ? "file" : "arguments/environment")} -> " +
            (ok ? "saved" : $"NOT saved ({readError ?? "file differs after writing"})"));
        return ok ? 0 : 1;
    }

    /// <summary>Clears everything this PC left retained on the broker, so it disappears from Home Assistant's discovery.</summary>
    public static async Task<int> PurgeAsync()
    {
        var cfg = ConfigStore.Load(out var error);
        if (error != null) { Console.Error.WriteLine(error); return 1; }
        var disc = new Discovery(cfg);
        var client = new MqttClientFactory().CreateMqttClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var b = new MqttClientOptionsBuilder()
                .WithTcpServer(cfg.Mqtt.Host, cfg.Mqtt.Port)
                .WithClientId($"hapcbridge-{cfg.DeviceId}-purge")
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(5));
            if (!string.IsNullOrEmpty(cfg.Mqtt.Username)) b.WithCredentials(cfg.Mqtt.Username, ConfigStore.GetPassword(cfg.Mqtt));
            if (cfg.Mqtt.UseTls)
                b.WithTlsOptions(o =>
                {
                    o.UseTls();
                    if (cfg.Mqtt.AllowUntrustedCertificates) o.WithCertificateValidationHandler(_ => true);
                });
            await client.ConnectAsync(b.Build(), cts.Token);

            var topics = new List<string> { disc.T.Availability, disc.T.Info, disc.T.ConfigState, disc.T.State };
            topics.AddRange(disc.All().Select(disc.ConfigTopic));
            foreach (var topic in topics)
            {
                var msg = new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(Array.Empty<byte>())
                    .WithRetainFlag(true).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build();
                await client.PublishAsync(msg, cts.Token);
            }
            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cts.Token);
            Console.WriteLine($"Cleared {topics.Count} topics for '{cfg.DeviceId}' on {cfg.Mqtt.Host}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Broker not reachable, nothing cleared: {ex.Message}");
            return 2;
        }
        finally { client.Dispose(); }
    }
}
