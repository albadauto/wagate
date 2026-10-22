using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WaGate.Shared;

namespace WaGate.Web.Services;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>Cliente tipado da WaGate API usado pelo painel.</summary>
public sealed class ApiClient(HttpClient http, AuthStateProvider auth)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---------- Auth ----------
    public Task<AuthResponse> LoginAsync(LoginRequest r) => Send<AuthResponse>(HttpMethod.Post, "api/auth/login", r, anonymous: true);
    public Task<AuthResponse> RegisterAsync(RegisterRequest r) => Send<AuthResponse>(HttpMethod.Post, "api/auth/register", r, anonymous: true);
    public Task<AccountDto> MeAsync() => Send<AccountDto>(HttpMethod.Get, "api/auth/me");
    public Task<AccountDto> SetClientTokenStateAsync(bool enabled) => Send<AccountDto>(HttpMethod.Put, "api/auth/client-token/state", new ClientTokenStateRequest { Enabled = enabled });
    public Task<AccountDto> RotateClientTokenAsync() => Send<AccountDto>(HttpMethod.Post, "api/auth/client-token/rotate");

    // ---------- Painel ----------
    public Task<DashboardDto> DashboardAsync() => Send<DashboardDto>(HttpMethod.Get, "api/dashboard");
    public Task<List<string>> EventsAsync() => Send<List<string>>(HttpMethod.Get, "api/events");

    // ---------- Instâncias ----------
    public Task<List<InstanceDto>> InstancesAsync() => Send<List<InstanceDto>>(HttpMethod.Get, "api/instances");
    public Task<InstanceDto> InstanceAsync(Guid id) => Send<InstanceDto>(HttpMethod.Get, $"api/instances/{id}");
    public Task<InstanceDto> CreateInstanceAsync(CreateInstanceRequest r) => Send<InstanceDto>(HttpMethod.Post, "api/instances", r);
    public Task<InstanceDto> UpdateInstanceAsync(Guid id, UpdateInstanceRequest r) => Send<InstanceDto>(HttpMethod.Put, $"api/instances/{id}", r);
    public Task DeleteInstanceAsync(Guid id) => Send<object>(HttpMethod.Delete, $"api/instances/{id}");
    public Task<InstanceDto> InstanceActionAsync(Guid id, string action) => Send<InstanceDto>(HttpMethod.Post, $"api/instances/{id}/{action}");
    public Task<QrCodeDto> QrAsync(Guid id) => Send<QrCodeDto>(HttpMethod.Get, $"api/instances/{id}/qr");
    public Task<PairingCodeDto> PairingCodeAsync(Guid id, string phone) => Send<PairingCodeDto>(HttpMethod.Post, $"api/instances/{id}/pairing-code", new PairingCodeRequest { Phone = phone });
    public Task<InstanceDto> RotateInstanceTokenAsync(Guid id) => Send<InstanceDto>(HttpMethod.Post, $"api/instances/{id}/token/rotate");
    public Task<SendResultDto> SendTextAsync(Guid id, SendTextRequest r) => Send<SendResultDto>(HttpMethod.Post, $"api/instances/{id}/send-text", r);

    public Task<PagedResult<MessageDto>> MessagesAsync(Guid id, int page, string? direction, string? search) =>
        Send<PagedResult<MessageDto>>(HttpMethod.Get,
            $"api/instances/{id}/messages?page={page}&pageSize=25{Q("direction", direction)}{Q("search", search)}");

    // ---------- Webhooks ----------
    public Task<List<WebhookDto>> WebhooksAsync(Guid instanceId) => Send<List<WebhookDto>>(HttpMethod.Get, $"api/instances/{instanceId}/webhooks");
    public Task<WebhookDto> CreateWebhookAsync(Guid instanceId, WebhookRequest r) => Send<WebhookDto>(HttpMethod.Post, $"api/instances/{instanceId}/webhooks", r);
    public Task<WebhookDto> UpdateWebhookAsync(Guid id, WebhookRequest r) => Send<WebhookDto>(HttpMethod.Put, $"api/webhooks/{id}", r);
    public Task DeleteWebhookAsync(Guid id) => Send<object>(HttpMethod.Delete, $"api/webhooks/{id}");
    public Task<WebhookDto> RotateWebhookSecretAsync(Guid id) => Send<WebhookDto>(HttpMethod.Post, $"api/webhooks/{id}/rotate-secret");
    public Task<DeliveryDetailDto> TestWebhookAsync(Guid id) => Send<DeliveryDetailDto>(HttpMethod.Post, $"api/webhooks/{id}/test");

    // ---------- Entregas ----------
    public Task<PagedResult<DeliveryDto>> DeliveriesAsync(int page, Guid? instanceId, string? status, string? evt) =>
        Send<PagedResult<DeliveryDto>>(HttpMethod.Get,
            $"api/deliveries?page={page}&pageSize=25{Q("instanceId", instanceId?.ToString())}{Q("status", status)}{Q("event", evt)}");
    public Task<DeliveryDetailDto> DeliveryAsync(Guid id) => Send<DeliveryDetailDto>(HttpMethod.Get, $"api/deliveries/{id}");
    public Task<DeliveryDto> RetryDeliveryAsync(Guid id) => Send<DeliveryDto>(HttpMethod.Post, $"api/deliveries/{id}/retry");

    // ---------- Planos e cobrança ----------
    public Task<List<PlanDto>> PlansAsync() => Send<List<PlanDto>>(HttpMethod.Get, "api/billing/plans", anonymous: true);
    public Task<SubscriptionDto> SubscriptionAsync() => Send<SubscriptionDto>(HttpMethod.Get, "api/billing/subscription");
    public Task<RedirectDto> CheckoutAsync(string plan, int quantity) => Send<RedirectDto>(HttpMethod.Post, "api/billing/checkout", new CheckoutRequest { Plan = plan, Quantity = quantity });
    public Task<SubscriptionDto> ChangePlanAsync(string plan, int quantity) => Send<SubscriptionDto>(HttpMethod.Post, "api/billing/change", new CheckoutRequest { Plan = plan, Quantity = quantity });
    public Task<RedirectDto> PortalAsync() => Send<RedirectDto>(HttpMethod.Post, "api/billing/portal");
    public Task<SubscriptionDto> SyncBillingAsync(string? sessionId) => Send<SubscriptionDto>(HttpMethod.Post, $"api/billing/sync{(sessionId is null ? "" : "?sessionId=" + Uri.EscapeDataString(sessionId))}");
    public Task<WahaStatusDto> WahaStatusAsync() => Send<WahaStatusDto>(HttpMethod.Get, "api/system/waha");

    /// <summary>URL pública da API (para exibir exemplos de integração).</summary>
    public string PublicBaseUrl { get; set; } = "";

    // ---------- Infra ----------
    private static string Q(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"&{name}={Uri.EscapeDataString(value)}";

    private async Task<T> Send<T>(HttpMethod method, string url, object? body = null, bool anonymous = false)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (!anonymous)
        {
            var token = auth.Token ?? throw new ApiException(HttpStatusCode.Unauthorized, "Sessão expirada. Faça login novamente.");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage res;
        try { res = await http.SendAsync(req); }
        catch (HttpRequestException ex)
        {
            throw new ApiException(HttpStatusCode.ServiceUnavailable, $"API indisponível ({ex.Message}).");
        }

        using (res)
        {
            if (res.StatusCode == HttpStatusCode.Unauthorized && !anonymous)
            {
                await auth.SignOutAsync();
                throw new ApiException(res.StatusCode, "Sessão expirada. Faça login novamente.");
            }
            if (!res.IsSuccessStatusCode) throw new ApiException(res.StatusCode, await ReadError(res));
            if (res.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default!;
            try { return (await res.Content.ReadFromJsonAsync<T>(Json))!; }
            catch (JsonException ex)
            {
                throw new ApiException(HttpStatusCode.BadGateway, $"Resposta inesperada da API ({ex.Message}).");
            }
        }
    }

    private static async Task<string> ReadError(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var msgs = errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString())).Where(s => s is not null);
                return string.Join(" ", msgs);
            }
            var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return string.Join(": ", new[] { title, detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(text) ? $"Erro {(int)res.StatusCode}" : text;
        }
    }
}

public sealed record WahaStatusDto(bool Reachable, bool ApiKeyValid, string? Version, string? Engine, string BaseUrl,
    string WebhookUrl, bool HmacConfigured, string? Error, DateTime? LastWebhookReceivedAt);
