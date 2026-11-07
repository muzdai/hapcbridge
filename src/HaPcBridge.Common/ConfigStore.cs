using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaPcBridge.Common;

public static class ConfigStore
{
    public static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HaPcBridge");

    public static readonly string ConfigPath = Path.Combine(Directory, "config.json");
    public static readonly string LogDirectory = Path.Combine(Directory, "logs");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Loads the config, creating a default file if missing. Never throws on a bad file: falls back to defaults.</summary>
    public static BridgeConfig Load(out string? error)
    {
        error = null;
        System.IO.Directory.CreateDirectory(Directory);

        BridgeConfig cfg;
        if (!File.Exists(ConfigPath))
        {
            cfg = new BridgeConfig();
            Normalize(cfg);
            try { Save(cfg); } catch { /* no write access (non-admin) — defaults in memory */ }
            return cfg;
        }

        try
        {
            var text = ReadShared(ConfigPath);
            cfg = JsonSerializer.Deserialize<BridgeConfig>(text, JsonOptions) ?? new BridgeConfig();
        }
        catch (Exception ex)
        {
            error = $"config.json is invalid, using defaults: {ex.Message}";
            cfg = new BridgeConfig();
        }

        Normalize(cfg);
        return cfg;
    }

    /// <summary>Service-side load: also moves a plain-text password into DPAPI storage.</summary>
    public static BridgeConfig LoadAndProtect(out string? error)
    {
        var cfg = Load(out error);
        if (error is null && !string.IsNullOrEmpty(cfg.Mqtt.Password))
        {
            cfg.Mqtt.PasswordProtected = Protect(cfg.Mqtt.Password);
            cfg.Mqtt.Password = null;
            try { Save(cfg); } catch { /* keep running with the in-memory copy */ }
        }
        return cfg;
    }

    public static void Save(BridgeConfig cfg)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions), Encoding.UTF8);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public static string GetPassword(MqttConfig mqtt)
    {
        if (!string.IsNullOrEmpty(mqtt.Password)) return mqtt.Password;
        if (string.IsNullOrEmpty(mqtt.PasswordProtected)) return "";
        try { return Unprotect(mqtt.PasswordProtected); }
        catch { return ""; }
    }

    static void Normalize(BridgeConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.DeviceName)) cfg.DeviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(cfg.DeviceId)) cfg.DeviceId = Environment.MachineName;
        cfg.DeviceId = Slug(cfg.DeviceId);
        cfg.BaseTopic = cfg.BaseTopic.Trim('/');
        cfg.DiscoveryPrefix = cfg.DiscoveryPrefix.Trim('/');
        cfg.UpdateIntervalSeconds = Math.Clamp(cfg.UpdateIntervalSeconds, 2, 3600);
        cfg.Commands.ShutdownDelaySeconds = Math.Clamp(cfg.Commands.ShutdownDelaySeconds, 0, 3600);
        cfg.Power.PsuEfficiency = Math.Clamp(cfg.Power.PsuEfficiency, 0.5, 1.0);
    }

    public static string Slug(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim().ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "pc" : r;
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HaPcBridge.mqtt");

    static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine));

    static string Unprotect(string b64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.LocalMachine));
}
