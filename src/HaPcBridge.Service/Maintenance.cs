using HaPcBridge.Common;
using HaPcBridge.Service.Mqtt;
using MQTTnet;
using MQTTnet.Protocol;

namespace HaPcBridge.Service;

/// <summary>One-shot commands used by the installer.</summary>
internal static class Maintenance
{
    /// <summary>Writes the broker settings entered in the installer. The password only ever comes from the environment.</summary>
    public static int Init(string[] args)
    {
        // --host H on the command line, or HAPCBRIDGE_MQTT_HOST in the environment (what the installer uses).
        string? Arg(string name)
        {
            var i = Array.FindIndex(args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Length) return args[i + 1];
            return Environment.GetEnvironmentVariable("HAPCBRIDGE_MQTT_" + name.ToUpperInvariant());
        }

        var cfg = ConfigStore.Load(out var error);
        if (error != null) { Console.Error.WriteLine(error); cfg = new BridgeConfig(); ConfigStore.Normalize(cfg); }

        if (Arg("host") is { Length: > 0 } host) cfg.Mqtt.Host = host.Trim();
        if (int.TryParse(Arg("port"), out var port) && port is > 0 and < 65536) cfg.Mqtt.Port = port;
        if (Arg("user") is { } user) cfg.Mqtt.Username = user.Trim();
        var password = Environment.GetEnvironmentVariable("HAPCBRIDGE_MQTT_PASSWORD");
        if (!string.IsNullOrEmpty(password)) { cfg.Mqtt.Password = password; cfg.Mqtt.PasswordProtected = null; }

        try { ConfigStore.Save(cfg); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        ConfigStore.LoadAndProtect(out _); // encrypt the password right away
        return 0;
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
