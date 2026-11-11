using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaPcBridge.Common;

/// <summary>A notification sent from Home Assistant to the PC.</summary>
public sealed class Notification
{
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>Identifies the notification in the answer event; a new one with the same tag replaces the old.</summary>
    public string Tag { get; set; } = "";

    /// <summary>Up to five buttons.</summary>
    public List<NotificationAction> Actions { get; set; } = new();

    /// <summary>Stay on screen for about 25 s instead of 7 s.</summary>
    public bool Long { get; set; }

    public bool Silent { get; set; }

    /// <summary>Accepts a JSON object, or plain text as the message.</summary>
    public static Notification? Parse(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0) return null;
        if (!payload.StartsWith('{')) return new Notification { Message = payload };
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject o) return null;
            var n = new Notification
            {
                Title = Str(o["title"]),
                Message = Str(o["message"]),
                Tag = Str(o["tag"]),
                Long = Str(o["duration"]).Equals("long", StringComparison.OrdinalIgnoreCase) || Bool(o["long"]),
                Silent = Bool(o["silent"]),
            };
            if (o["actions"] is JsonArray actions)
            {
                foreach (var a in actions.Take(5))
                {
                    // {"id": "open", "title": "Open"} or just "Open"
                    if (a is JsonObject ao)
                    {
                        var title = Str(ao["title"]);
                        var id = Str(ao["id"]);
                        if (id.Length == 0) id = Str(ao["action"]);
                        if (title.Length == 0) title = id;
                        if (title.Length > 0) n.Actions.Add(new NotificationAction { Id = id.Length > 0 ? id : title, Title = title });
                    }
                    else if (a != null && Str(a) is { Length: > 0 } t)
                        n.Actions.Add(new NotificationAction { Id = t, Title = t });
                }
            }
            if (n.Tag.Length > 64) n.Tag = n.Tag[..64];
            return n.Title.Length == 0 && n.Message.Length == 0 ? null : n;
        }
        catch (JsonException) { return null; }
    }

    static string Str(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()).Trim() : "";
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public string ToJson() => JsonSerializer.Serialize(this, Ipc.Json);
    public static Notification? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<Notification>(json, Ipc.Json); } catch { return null; }
    }
}

public sealed class NotificationAction
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}
