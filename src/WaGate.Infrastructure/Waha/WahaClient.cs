using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WaGate.Infrastructure.Waha;

public interface IWahaClient
{
    Task<WahaSession?> GetSessionAsync(string session, CancellationToken ct = default);
    Task<IReadOnlyList<WahaSession>> ListSessionsAsync(CancellationToken ct = default);
    Task<WahaSession> UpsertSessionAsync(string session, Guid instanceId, bool start, CancellationToken ct = default);
    Task StartAsync(string session, CancellationToken ct = default);
    Task StopAsync(string session, CancellationToken ct = default);
    Task RestartAsync(string session, CancellationToken ct = default);
    Task LogoutAsync(string session, CancellationToken ct = default);
    Task DeleteAsync(string session, CancellationToken ct = default);
    Task<WahaQr?> GetQrAsync(string session, CancellationToken ct = default);
    Task<string?> RequestPairingCodeAsync(string session, string phone, CancellationToken ct = default);
    Task<WahaMe?> GetMeAsync(string session, CancellationToken ct = default);

    Task<JsonElement> SendTextAsync(string session, string chatId, string text, string? replyTo, bool linkPreview, IEnumerable<string>? mentions, CancellationToken ct = default);
    Task<JsonElement> SendImageAsync(string session, string chatId, WahaFile file, string? caption, string? replyTo, CancellationToken ct = default);
    Task<JsonElement> SendFileAsync(string session, string chatId, WahaFile file, string? caption, string? replyTo, CancellationToken ct = default);
    Task<JsonElement> SendVoiceAsync(string session, string chatId, WahaFile file, bool convert, string? replyTo, CancellationToken ct = default);
    Task<JsonElement> SendVideoAsync(string session, string chatId, WahaFile file, string? caption, bool convert, bool asNote, string? replyTo, CancellationToken ct = default);
    Task<JsonElement> SendLocationAsync(string session, string chatId, double lat, double lng, string? title, string? replyTo, CancellationToken ct = default);
    Task<JsonElement> SendContactAsync(string session, string chatId, string fullName, string phone, string? organization, string? replyTo, CancellationToken ct = default);
    Task ReactAsync(string session, string messageId, string reaction, CancellationToken ct = default);
    Task SendSeenAsync(string session, string chatId, IEnumerable<string>? messageIds, CancellationToken ct = default);
    Task TypingAsync(string session, string chatId, bool start, CancellationToken ct = default);
    Task<WahaCheckExists> CheckExistsAsync(string session, string phone, CancellationToken ct = default);
    Task RejectCallAsync(string session, string from, string callId, CancellationToken ct = default);
    Task<WahaDiagnostics> DiagnoseAsync(CancellationToken ct = default);
}

/// <summary>Resultado do diagnóstico de conexão com o WAHA.</summary>
public sealed record WahaDiagnostics(
    bool Reachable, bool ApiKeyValid, string? Version, string? Engine, string BaseUrl, string WebhookUrl,
    bool HmacConfigured, string? Error);

public sealed class WahaClient(HttpClient http, IOptions<WahaOptions> options, ILogger<WahaClient> logger) : IWahaClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly WahaOptions _opt = options.Value;

    // ---------------- Sessões ----------------

    public async Task<WahaSession?> GetSessionAsync(string session, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"api/sessions/{Uri.EscapeDataString(session)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOk(res, ct);
        return await res.Content.ReadFromJsonAsync<WahaSession>(Json, ct);
    }

    public async Task<IReadOnlyList<WahaSession>> ListSessionsAsync(CancellationToken ct = default)
    {
        using var res = await http.GetAsync("api/sessions?all=true", ct);
        await EnsureOk(res, ct);
        return await res.Content.ReadFromJsonAsync<List<WahaSession>>(Json, ct) ?? [];
    }

    public async Task<WahaSession> UpsertSessionAsync(string session, Guid instanceId, bool start, CancellationToken ct = default)
    {
        var config = BuildConfig(instanceId);
        var existing = await GetSessionAsync(session, ct);

        if (existing is null)
        {
            var body = new JsonObject { ["name"] = session, ["start"] = start, ["config"] = config };
            var created = await Send<WahaSession>(HttpMethod.Post, "api/sessions", body, ct);
            return created ?? new WahaSession { Name = session, Status = start ? "STARTING" : "STOPPED" };
        }

        // Atenção: no WAHA o PUT /api/sessions/{nome} PARA e reinicia a sessão.
        // Só reconfiguramos quando a URL/eventos/HMAC do webhook realmente mudaram.
        if (!WebhookConfigMatches(existing.Config, _opt))
        {
            logger.LogInformation("Reconfigurando webhook da sessão {Session} no WAHA", session);
            await Send<JsonElement>(HttpMethod.Put, $"api/sessions/{Uri.EscapeDataString(session)}", new JsonObject { ["config"] = config }, ct);
            existing = await GetSessionAsync(session, ct) ?? existing;
        }

        if (start)
        {
            if (existing.Status == "STOPPED") await StartAsync(session, ct);
            else if (existing.Status == "FAILED") await RestartAsync(session, ct);
            else return existing; // STARTING / SCAN_QR_CODE / WORKING: já está rodando
        }
        return await GetSessionAsync(session, ct) ?? existing;
    }

    /// <summary>Compara o webhook configurado na sessão com o esperado (URL, eventos e chave HMAC).</summary>
    public static bool WebhookConfigMatches(JsonElement? config, WahaOptions opt)
    {
        if (config is not { ValueKind: JsonValueKind.Object } cfg ||
            !cfg.TryGetProperty("webhooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var hook in hooks.EnumerateArray())
        {
            if (!hook.TryGetProperty("url", out var url) || url.GetString() != opt.WebhookUrl) continue;

            var events = hook.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array
                ? ev.EnumerateArray().Select(e => e.GetString()).ToHashSet()
                : [];
            if (!events.SetEquals(opt.Events)) return false;

            var key = hook.TryGetProperty("hmac", out var h) && h.ValueKind == JsonValueKind.Object &&
                      h.TryGetProperty("key", out var k) ? k.GetString() : null;
            if (key is not null && key.Contains('*')) return true; // chave mascarada: não dá para comparar
            return string.IsNullOrEmpty(opt.WebhookHmacKey) ? string.IsNullOrEmpty(key) : key == opt.WebhookHmacKey;
        }
        return false;
    }

    private JsonObject BuildConfig(Guid instanceId)
    {
        var webhook = new JsonObject
        {
            ["url"] = _opt.WebhookUrl,
            ["events"] = new JsonArray(_opt.Events.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
            ["retries"] = new JsonObject { ["policy"] = "exponential", ["delaySeconds"] = 2, ["attempts"] = 6 }
        };
        if (!string.IsNullOrWhiteSpace(_opt.WebhookHmacKey))
            webhook["hmac"] = new JsonObject { ["key"] = _opt.WebhookHmacKey };

        return new JsonObject
        {
            ["metadata"] = new JsonObject { ["instanceId"] = instanceId.ToString() },
            ["webhooks"] = new JsonArray(webhook)
        };
    }

    public Task StartAsync(string session, CancellationToken ct = default) => Post($"api/sessions/{Uri.EscapeDataString(session)}/start", ct);
    public Task StopAsync(string session, CancellationToken ct = default) => Post($"api/sessions/{Uri.EscapeDataString(session)}/stop", ct);
    public Task RestartAsync(string session, CancellationToken ct = default) => Post($"api/sessions/{Uri.EscapeDataString(session)}/restart", ct);
    public Task LogoutAsync(string session, CancellationToken ct = default) => Post($"api/sessions/{Uri.EscapeDataString(session)}/logout", ct);

    public async Task DeleteAsync(string session, CancellationToken ct = default)
    {
        using var res = await http.DeleteAsync($"api/sessions/{Uri.EscapeDataString(session)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureOk(res, ct);
    }

    public async Task<WahaQr?> GetQrAsync(string session, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"api/{Uri.EscapeDataString(session)}/auth/qr?format=image");
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return null;
        await EnsureOk(res, ct);

        var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.StartsWith("image/"))
        {
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return new WahaQr { Mimetype = mediaType, Data = Convert.ToBase64String(bytes) };
        }
        return await res.Content.ReadFromJsonAsync<WahaQr>(Json, ct);
    }

    public async Task<string?> RequestPairingCodeAsync(string session, string phone, CancellationToken ct = default)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var r = await Send<WahaPairingCode>(HttpMethod.Post, $"api/{Uri.EscapeDataString(session)}/auth/request-code",
            new JsonObject { ["phoneNumber"] = digits }, ct);
        return r?.Code;
    }

    public async Task<WahaMe?> GetMeAsync(string session, CancellationToken ct = default)
    {
        using var res = await http.GetAsync($"api/sessions/{Uri.EscapeDataString(session)}/me", ct);
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text) || text == "null") return null;
        return JsonSerializer.Deserialize<WahaMe>(text, Json);
    }

    // ---------------- Mensagens ----------------

    public Task<JsonElement> SendTextAsync(string session, string chatId, string text, string? replyTo, bool linkPreview, IEnumerable<string>? mentions, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["text"] = text;
        body["linkPreview"] = linkPreview;
        if (mentions is not null)
        {
            var list = mentions.ToList();
            if (list.Count == 1 && list[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                body["mentions"] = new JsonArray("all");
            else if (list.Count > 0)
                body["mentions"] = new JsonArray(list.Select(m => (JsonNode)JsonValue.Create(Whatsapp.ChatIds.ToChatId(m))!).ToArray());
        }
        return SendJson("api/sendText", body, ct);
    }

    public Task<JsonElement> SendImageAsync(string session, string chatId, WahaFile file, string? caption, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["file"] = FileNode(file);
        if (caption is not null) body["caption"] = caption;
        return SendJson("api/sendImage", body, ct);
    }

    public Task<JsonElement> SendFileAsync(string session, string chatId, WahaFile file, string? caption, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["file"] = FileNode(file);
        if (caption is not null) body["caption"] = caption;
        return SendJson("api/sendFile", body, ct);
    }

    public Task<JsonElement> SendVoiceAsync(string session, string chatId, WahaFile file, bool convert, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["file"] = FileNode(file);
        body["convert"] = convert;
        return SendJson("api/sendVoice", body, ct);
    }

    public Task<JsonElement> SendVideoAsync(string session, string chatId, WahaFile file, string? caption, bool convert, bool asNote, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["file"] = FileNode(file);
        body["convert"] = convert;
        body["asNote"] = asNote;
        if (caption is not null) body["caption"] = caption;
        return SendJson("api/sendVideo", body, ct);
    }

    public Task<JsonElement> SendLocationAsync(string session, string chatId, double lat, double lng, string? title, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        body["latitude"] = lat;
        body["longitude"] = lng;
        body["title"] = title ?? "";
        return SendJson("api/sendLocation", body, ct);
    }

    public Task<JsonElement> SendContactAsync(string session, string chatId, string fullName, string phone, string? organization, string? replyTo, CancellationToken ct = default)
    {
        var body = Base(session, chatId, replyTo);
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        var contact = new JsonObject { ["fullName"] = fullName, ["phoneNumber"] = "+" + digits, ["whatsappId"] = digits };
        if (organization is not null) contact["organization"] = organization;
        body["contacts"] = new JsonArray(contact);
        return SendJson("api/sendContactVcard", body, ct);
    }

    public async Task ReactAsync(string session, string messageId, string reaction, CancellationToken ct = default)
        => await Send<JsonElement>(HttpMethod.Put, "api/reaction",
            new JsonObject { ["session"] = session, ["messageId"] = messageId, ["reaction"] = reaction }, ct);

    public async Task SendSeenAsync(string session, string chatId, IEnumerable<string>? messageIds, CancellationToken ct = default)
    {
        var body = new JsonObject { ["session"] = session, ["chatId"] = chatId };
        var ids = messageIds?.ToList();
        if (ids is { Count: > 0 }) body["messageIds"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        await Send<JsonElement>(HttpMethod.Post, "api/sendSeen", body, ct);
    }

    public async Task TypingAsync(string session, string chatId, bool start, CancellationToken ct = default)
        => await Send<JsonElement>(HttpMethod.Post, start ? "api/startTyping" : "api/stopTyping",
            new JsonObject { ["session"] = session, ["chatId"] = chatId }, ct);

    public async Task<WahaCheckExists> CheckExistsAsync(string session, string phone, CancellationToken ct = default)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        using var res = await http.GetAsync($"api/contacts/check-exists?phone={digits}&session={Uri.EscapeDataString(session)}", ct);
        await EnsureOk(res, ct);
        return await res.Content.ReadFromJsonAsync<WahaCheckExists>(Json, ct) ?? new WahaCheckExists();
    }

    public async Task RejectCallAsync(string session, string from, string callId, CancellationToken ct = default)
        => await Send<JsonElement>(HttpMethod.Post, $"api/{Uri.EscapeDataString(session)}/calls/reject",
            new JsonObject { ["from"] = from, ["id"] = callId }, ct);

    public async Task<WahaDiagnostics> DiagnoseAsync(CancellationToken ct = default)
    {
        string? Fail(Exception ex) => ex switch
        {
            HttpRequestException h => $"Não foi possível conectar em {_opt.BaseUrl}: {h.Message}",
            TaskCanceledException => $"Tempo esgotado conectando em {_opt.BaseUrl}",
            _ => ex.Message
        };

        // 1) alcance: /ping não exige API key
        try
        {
            using var ping = await http.GetAsync("ping", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Polly.CircuitBreaker.BrokenCircuitException or Polly.Timeout.TimeoutRejectedException)
        {
            return new WahaDiagnostics(false, false, null, null, _opt.BaseUrl, _opt.WebhookUrl, !string.IsNullOrEmpty(_opt.WebhookHmacKey), Fail(ex));
        }

        // 2) API key: /api/server/version exige X-Api-Key
        using var res = await http.GetAsync("api/server/version", ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
            return new WahaDiagnostics(true, false, null, null, _opt.BaseUrl, _opt.WebhookUrl, !string.IsNullOrEmpty(_opt.WebhookHmacKey),
                "O WAHA recusou a API key. Confira se Waha:ApiKey é igual ao WAHA_API_KEY do container (valores comuns como 'admin' ou '123' são trocados por uma chave aleatória pelo WAHA).");

        string? version = null, engine = null;
        if (res.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                engine = doc.RootElement.TryGetProperty("engine", out var e) ? e.GetString() : null;
            }
            catch (JsonException) { }
        }
        return new WahaDiagnostics(true, res.IsSuccessStatusCode, version, engine, _opt.BaseUrl, _opt.WebhookUrl,
            !string.IsNullOrEmpty(_opt.WebhookHmacKey), res.IsSuccessStatusCode ? null : $"WAHA respondeu {(int)res.StatusCode}");
    }

    // ---------------- Helpers ----------------

    private static JsonObject Base(string session, string chatId, string? replyTo)
    {
        var o = new JsonObject { ["session"] = session, ["chatId"] = chatId };
        if (!string.IsNullOrWhiteSpace(replyTo)) o["reply_to"] = replyTo;
        return o;
    }

    private static JsonObject FileNode(WahaFile f)
    {
        var o = new JsonObject();
        if (f.Mimetype is not null) o["mimetype"] = f.Mimetype;
        if (f.Filename is not null) o["filename"] = f.Filename;
        if (f.Url is not null) o["url"] = f.Url;
        if (f.Data is not null) o["data"] = f.Data;
        return o;
    }

    private async Task<JsonElement> SendJson(string path, JsonObject body, CancellationToken ct)
        => await Send<JsonElement>(HttpMethod.Post, path, body, ct);

    private async Task Post(string path, CancellationToken ct)
    {
        using var res = await http.PostAsync(path, null, ct);
        await EnsureOk(res, ct);
    }

    private async Task<T?> Send<T>(HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: Json) };
        using var res = await http.SendAsync(req, ct);
        await EnsureOk(res, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try { return JsonSerializer.Deserialize<T>(text, Json); }
        catch (JsonException) { return default; }
    }

    private async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        logger.LogWarning("WAHA {Method} {Path} → {Status}: {Body}",
            res.RequestMessage?.Method, res.RequestMessage?.RequestUri?.PathAndQuery, (int)res.StatusCode, Truncate(body, 500));
        throw new WahaApiException((int)res.StatusCode, $"WAHA respondeu {(int)res.StatusCode}: {Truncate(ExtractMessage(body), 300)}", body);
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.ValueKind == JsonValueKind.String ? m.GetString()! : m.ToString();
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.ToString();
        }
        catch (JsonException) { }
        return body;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// Extrai o id da mensagem da resposta do WAHA. O formato varia por engine:
    /// WEBJS → id._serialized ou id (string); NOWEB/GOWS → key.id ou id.
    /// </summary>
    public static string? ExtractMessageId(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;
        if (response.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.String) return id.GetString();
            if (id.ValueKind == JsonValueKind.Object && id.TryGetProperty("_serialized", out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();
            if (id.ValueKind == JsonValueKind.Object && id.TryGetProperty("id", out var inner) && inner.ValueKind == JsonValueKind.String)
                return inner.GetString();
        }
        if (response.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object &&
            key.TryGetProperty("id", out var kid) && kid.ValueKind == JsonValueKind.String)
            return kid.GetString();
        if (response.TryGetProperty("_data", out var data) && data.ValueKind == JsonValueKind.Object)
            return ExtractMessageId(data);
        return null;
    }
}
