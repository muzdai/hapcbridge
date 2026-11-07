using System.Text.Json;
using System.Text.Json.Serialization;

namespace HaPcBridge.Common;

/// <summary>Newline-delimited JSON messages between the service and the user-session agent.</summary>
public static class Ipc
{
    public const string PipeName = "HaPcBridge.Agent";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(IpcMessage m) => JsonSerializer.Serialize(m, Json);

    public static IpcMessage? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(line, Json); }
        catch { return null; }
    }
}

public sealed class IpcMessage
{
    /// <summary>agent → service: "hello", "status". service → agent: "command", "config".</summary>
    public string Type { get; set; } = "";

    // hello
    public int? SessionId { get; set; }
    public string? User { get; set; }

    // status
    public string? ActiveWindow { get; set; }
    public string? ActiveProcess { get; set; }
    public double? IdleSeconds { get; set; }
    public bool? Locked { get; set; }

    // command
    public string? Command { get; set; }

    // config
    public bool? TrayIcon { get; set; }
    public string? StatusText { get; set; }

    public static IpcMessage Cmd(string command) => new() { Type = "command", Command = command };
}

public static class AgentCommands
{
    public const string CloseActiveWindow = "close_active_window";
    public const string Lock = "lock";
    public const string MonitorOff = "monitor_off";
    public const string Exit = "exit";
}
