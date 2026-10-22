using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaGate.Domain;

namespace WaGate.Infrastructure.Billing;

public sealed class StripeException(int status, string message, string? code) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>
/// Cliente mínimo da API REST do Stripe (form-encoded), sem dependência do Stripe.net.
/// Cobre: clientes, preços (por lookup_key), Checkout, Portal e assinaturas.
/// </summary>
public sealed class StripeClient(HttpClient http, IOptions<StripeOptions> stripe, IOptions<BillingOptions> billing, ILogger<StripeClient> logger)
{
    private static readonly ConcurrentDictionary<string, string> PriceCache = new();

    private readonly StripeOptions _stripe = stripe.Value;
    private readonly BillingOptions _billing = billing.Value;

    // ---------------- Clientes ----------------

    public async Task<string> CreateCustomerAsync(Account a, CancellationToken ct)
    {
        var f = new List<KeyValuePair<string, string>>
        {
            new("email", a.Email),
            new("name", a.CompanyName ?? a.Name),
            new("metadata[accountId]", a.Id.ToString()),
            new("preferred_locales[0]", "pt-BR")
        };
        if (a.Phone is not null) f.Add(new("phone", "+" + a.Phone));
        if (a.Document is { Length: 11 or 14 } doc)
        {
            f.Add(new("tax_id_data[0][type]", doc.Length == 11 ? "br_cpf" : "br_cnpj"));
            f.Add(new("tax_id_data[0][value]", Shared.BrDocs.FormatDocument(doc)));
        }

        try
        {
            using var r = await Post("v1/customers", f, ct, idempotencyKey: $"customer-{a.Id}");
            return r.RootElement.GetProperty("id").GetString()!;
        }
        catch (StripeException ex) when (ex.Code is "tax_id_invalid" or "invalid_tax_id")
        {
            // não impede a assinatura se o Stripe recusar o documento
            logger.LogWarning("Stripe recusou o documento da conta {Account}; criando cliente sem tax id", a.Id);
            f.RemoveAll(kv => kv.Key.StartsWith("tax_id_data"));
            using var r = await Post("v1/customers", f, ct, idempotencyKey: $"customer-{a.Id}-notax");
            return r.RootElement.GetProperty("id").GetString()!;
        }
    }

    // ---------------- Preços ----------------

    /// <summary>Resolve (ou cria) o preço mensal por instância do plano, identificado pelo lookup_key.</summary>
    public async Task<string> ResolvePriceAsync(BillingPlan plan, CancellationToken ct)
    {
        var p = _billing.Get(plan);
        if (!string.IsNullOrWhiteSpace(p.PriceId)) return p.PriceId;

        var key = p.LookupKey(plan, _billing.Currency);
        if (PriceCache.TryGetValue(key, out var cached)) return cached;

        using (var found = await Get($"v1/prices?active=true&lookup_keys[]={Uri.EscapeDataString(key)}", ct))
        {
            var data = found.RootElement.GetProperty("data");
            if (data.GetArrayLength() > 0)
                return PriceCache[key] = data[0].GetProperty("id").GetString()!;
        }

        using var created = await Post("v1/prices",
        [
            new("currency", _billing.Currency),
            new("unit_amount", p.UnitAmount.ToString(CultureInfo.InvariantCulture)),
            new("recurring[interval]", "month"),
            new("recurring[usage_type]", "licensed"),
            new("product_data[name]", $"WaGate {p.Name}"),
            new("product_data[metadata][plan]", plan.ToString()),
            new("nickname", $"{p.Name} por instância/mês"),
            new("lookup_key", key),
            new("metadata[plan]", plan.ToString())
        ], ct, idempotencyKey: $"price-{key}");
        logger.LogInformation("Preço {Key} criado no Stripe", key);
        return PriceCache[key] = created.RootElement.GetProperty("id").GetString()!;
    }

    // ---------------- Checkout / Portal ----------------

    public async Task<string> CreateCheckoutAsync(Account a, BillingPlan plan, int quantity, bool withTrial, CancellationToken ct)
    {
        var p = _billing.Get(plan);
        var price = await ResolvePriceAsync(plan, ct);
        var app = _billing.PublicAppUrl.TrimEnd('/');

        var f = new List<KeyValuePair<string, string>>
        {
            new("mode", "subscription"),
            new("customer", a.StripeCustomerId!),
            new("client_reference_id", a.Id.ToString()),
            new("line_items[0][price]", price),
            new("line_items[0][quantity]", quantity.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][adjustable_quantity][enabled]", "true"),
            new("line_items[0][adjustable_quantity][minimum]", p.MinQuantity.ToString(CultureInfo.InvariantCulture)),
            new("line_items[0][adjustable_quantity][maximum]", p.MaxQuantity.ToString(CultureInfo.InvariantCulture)),
            new("subscription_data[metadata][accountId]", a.Id.ToString()),
            new("subscription_data[metadata][plan]", plan.ToString()),
            new("metadata[accountId]", a.Id.ToString()),
            new("metadata[plan]", plan.ToString()),
            new("success_url", $"{app}/billing/success?session_id={{CHECKOUT_SESSION_ID}}"),
            new("cancel_url", $"{app}/plans?canceled=1"),
            new("locale", "pt-BR"),
            new("allow_promotion_codes", "true"),
            new("billing_address_collection", "auto")
        };
        if (withTrial && p.TrialDays > 0)
        {
            f.Add(new("subscription_data[trial_period_days]", p.TrialDays.ToString(CultureInfo.InvariantCulture)));
            // o cartão é pedido já no início do teste; sem cartão ao fim do teste, cancela
            f.Add(new("payment_method_collection", "always"));
            f.Add(new("subscription_data[trial_settings][end_behavior][missing_payment_method]", "cancel"));
        }

        using var r = await Post("v1/checkout/sessions", f, ct);
        return r.RootElement.GetProperty("url").GetString()!;
    }

    public async Task<string> CreatePortalAsync(string customerId, CancellationToken ct)
    {
        using var r = await Post("v1/billing_portal/sessions",
        [
            new("customer", customerId),
            new("return_url", _billing.PublicAppUrl.TrimEnd('/') + "/billing"),
            new("locale", "pt-BR")
        ], ct);
        return r.RootElement.GetProperty("url").GetString()!;
    }

    public async Task<string?> GetCheckoutSubscriptionIdAsync(string sessionId, CancellationToken ct)
    {
        using var r = await Get($"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}", ct);
        return r.RootElement.TryGetProperty("subscription", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    // ---------------- Assinaturas ----------------

    public async Task<StripeSubscription> GetSubscriptionAsync(string id, CancellationToken ct)
    {
        using var r = await Get($"v1/subscriptions/{Uri.EscapeDataString(id)}", ct);
        return StripeSubscription.Parse(r.RootElement);
    }

    /// <summary>Troca plano e/ou quantidade, com cobrança proporcional.</summary>
    public async Task<StripeSubscription> UpdateSubscriptionAsync(StripeSubscription current, BillingPlan plan, int quantity, bool endTrial, CancellationToken ct)
    {
        var price = await ResolvePriceAsync(plan, ct);
        var f = new List<KeyValuePair<string, string>>
        {
            new("items[0][id]", current.ItemId),
            new("items[0][price]", price),
            new("items[0][quantity]", quantity.ToString(CultureInfo.InvariantCulture)),
            new("proration_behavior", "create_prorations"),
            new("metadata[plan]", plan.ToString())
        };
        if (endTrial) f.Add(new("trial_end", "now"));
        using var r = await Post($"v1/subscriptions/{Uri.EscapeDataString(current.Id)}", f, ct);
        return StripeSubscription.Parse(r.RootElement);
    }

    // ---------------- Webhook ----------------

    /// <summary>
    /// Valida o header Stripe-Signature: t=timestamp,v1=hex(HMAC-SHA256(secret, "t.payload")).
    /// </summary>
    public static bool VerifySignature(string payload, string? header, string secret, TimeSpan tolerance, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrWhiteSpace(secret)) return false;

        long? ts = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t" && long.TryParse(kv[1], out var t)) ts = t;
            else if (kv[0].Trim() == "v1") signatures.Add(kv[1].Trim());
        }
        if (ts is null || signatures.Count == 0) return false;

        var age = (now ?? DateTimeOffset.UtcNow) - DateTimeOffset.FromUnixTimeSeconds(ts.Value);
        if (age.Duration() > tolerance) return false;

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ts}.{payload}"));
        foreach (var sig in signatures)
        {
            byte[] provided;
            try { provided = Convert.FromHexString(sig); } catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(expected, provided)) return true;
        }
        return false;
    }

    // ---------------- HTTP ----------------

    private async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        return await Send(req, ct);
    }

    private async Task<JsonDocument> Post(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct, string? idempotencyKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return await Send(req, ct);
    }

    private async Task<JsonDocument> Send(HttpRequestMessage req, CancellationToken ct)
    {
        if (!_stripe.Enabled) throw new StripeException(503, "Stripe não configurado (Stripe:SecretKey).", "not_configured");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _stripe.SecretKey);
        req.Headers.Add("Stripe-Version", _stripe.ApiVersion);

        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode) return JsonDocument.Parse(body);

        string message = $"Stripe respondeu {(int)res.StatusCode}", code = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                message = e.TryGetProperty("message", out var m) ? m.GetString() ?? message : message;
                code = e.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            }
        }
        catch (JsonException) { }
        logger.LogWarning("Stripe {Method} {Path} → {Status}: {Message}", req.Method, req.RequestUri, (int)res.StatusCode, message);
        throw new StripeException((int)res.StatusCode, message, code);
    }
}

/// <summary>Dados da assinatura relevantes para o WaGate.</summary>
public sealed record StripeSubscription(
    string Id, string CustomerId, string Status, string ItemId, int Quantity, string? LookupKey,
    string? MetadataPlan, string? MetadataAccountId, DateTime? TrialEnd, DateTime? CurrentPeriodEnd, bool CancelAtPeriodEnd)
{
    public BillingPlan Plan =>
        Enum.TryParse<BillingPlan>(MetadataPlan, true, out var p) ? p
        : LookupKey?.Contains("_partner_") == true ? BillingPlan.Partner
        : LookupKey?.Contains("_ultimate_") == true ? BillingPlan.Ultimate
        : BillingPlan.None;

    public static StripeSubscription Parse(JsonElement s)
    {
        static DateTime? Unix(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()).UtcDateTime : null;
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var item = s.GetProperty("items").GetProperty("data")[0];
        var price = item.GetProperty("price");
        var meta = s.TryGetProperty("metadata", out var m) ? m : default;
        var customer = s.GetProperty("customer");

        return new StripeSubscription(
            s.GetProperty("id").GetString()!,
            customer.ValueKind == JsonValueKind.String ? customer.GetString()! : customer.GetProperty("id").GetString()!,
            s.GetProperty("status").GetString()!,
            item.GetProperty("id").GetString()!,
            item.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number ? q.GetInt32() : 1,
            Str(price, "lookup_key"),
            Str(meta, "plan"),
            Str(meta, "accountId"),
            Unix(s, "trial_end"),
            // versões novas da API movem current_period_end para o item
            Unix(s, "current_period_end") ?? Unix(item, "current_period_end"),
            s.TryGetProperty("cancel_at_period_end", out var c) && c.ValueKind == JsonValueKind.True);
    }
}
