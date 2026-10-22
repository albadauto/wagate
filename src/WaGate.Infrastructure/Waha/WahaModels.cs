using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaGate.Infrastructure.Waha;

public sealed class WahaSession
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("engine")] public JsonElement? Engine { get; set; }
    [JsonPropertyName("me")] public WahaMe? Me { get; set; }
    [JsonPropertyName("config")] public JsonElement? Config { get; set; }
}

public sealed class WahaMe
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("pushName")] public string? PushName { get; set; }
}

public sealed class WahaQr
{
    [JsonPropertyName("mimetype")] public string? Mimetype { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

public sealed class WahaCheckExists
{
    [JsonPropertyName("numberExists")] public bool NumberExists { get; set; }
    [JsonPropertyName("chatId")] public string? ChatId { get; set; }
}

public sealed class WahaPairingCode
{
    [JsonPropertyName("code")] public string? Code { get; set; }
}

public sealed record WahaFile(
    [property: JsonPropertyName("mimetype")] string? Mimetype,
    [property: JsonPropertyName("filename")] string? Filename,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("data")] string? Data);

/// <summary>Envelope dos webhooks enviados pelo WAHA.</summary>
public sealed class WahaEnvelope
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }
    [JsonPropertyName("event")] public string Event { get; set; } = "";
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("me")] public WahaMe? Me { get; set; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; set; }
    [JsonPropertyName("engine")] public string? Engine { get; set; }
}

public sealed class WahaApiException(int statusCode, string message, string? body)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Body { get; } = body;
}
