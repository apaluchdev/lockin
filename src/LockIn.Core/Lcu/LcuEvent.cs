using System.Text.Json;

namespace LockIn.Core.Lcu;

/// <summary>A change pushed by the client over its WAMP WebSocket.</summary>
/// <param name="Uri">The API path that changed, e.g. <c>/lol-champ-select/v1/session</c>.</param>
/// <param name="EventType"><c>Create</c>, <c>Update</c> or <c>Delete</c>.</param>
/// <param name="Data">The resource's new value (null/undefined on Delete).</param>
public sealed record LcuEvent(string Uri, string EventType, JsonElement Data)
{
    public bool IsDelete => string.Equals(EventType, "Delete", StringComparison.OrdinalIgnoreCase);

    public T? DataAs<T>() =>
        IsDelete || Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default
            : Data.Deserialize<T>(LcuClient.JsonOptions);
}

/// <summary>The client resources LockIn listens to.</summary>
public static class LcuEndpoints
{
    public const string GameflowPhase = "/lol-gameflow/v1/gameflow-phase";
    public const string GameflowSession = "/lol-gameflow/v1/session";
    public const string ReadyCheck = "/lol-matchmaking/v1/ready-check";
    public const string ChampSelectSession = "/lol-champ-select/v1/session";

    /// <summary>Resources whose changes are subscribed to on the WebSocket.</summary>
    public static IReadOnlyList<string> Subscribed { get; } = [GameflowPhase, ReadyCheck, ChampSelectSession];

    /// <summary>The WAMP event name for a resource: "/lol-a/v1/b" → "OnJsonApiEvent_lol-a_v1_b".</summary>
    public static string EventName(string uri) => "OnJsonApiEvent" + uri.Replace('/', '_');
}

internal static class LcuEventParser
{
    private const int WampEventOpcode = 8;

    /// <summary>Parses a WAMP event frame: <c>[8, "OnJsonApiEvent_…", {"uri":…, "eventType":…, "data":…}]</c>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out LcuEvent? lcuEvent)
    {
        lcuEvent = null;
        if (utf8Json.IsEmpty)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array
                || root.GetArrayLength() < 3
                || root[0].ValueKind != JsonValueKind.Number
                || root[0].GetInt32() != WampEventOpcode)
            {
                return false;
            }

            var payload = root[2];
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("uri", out var uri)
                || uri.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var eventType = payload.TryGetProperty("eventType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()!
                : "Update";
            var data = payload.TryGetProperty("data", out var d) ? d.Clone() : default;
            lcuEvent = new LcuEvent(uri.GetString()!, eventType, data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
