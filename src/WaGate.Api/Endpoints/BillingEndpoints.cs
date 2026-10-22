using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaGate.Api.Auth;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Billing;
using WaGate.Infrastructure.Data;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

public static class BillingEndpoints
{
    public static void MapBilling(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/billing").WithTags("Planos e cobrança");

        g.MapGet("/plans", (BillingService billing) => Results.Ok(billing.Plans()))
            .AllowAnonymous()
            .WithSummary("Planos disponíveis e preços (centavos por instância/mês)");

        g.MapGet("/subscription", async (HttpContext http, WaGateDbContext db, BillingService billing, CancellationToken ct) =>
            {
                var a = await Account(http, db, ct);
                return a is null ? Results.Unauthorized() : Results.Ok(await billing.GetAsync(a, ct));
            })
            .RequireAuthorization()
            .WithSummary("Assinatura atual da conta");

        g.MapPost("/checkout", async (CheckoutRequest req, HttpContext http, WaGateDbContext db, BillingService billing, CancellationToken ct) =>
            {
                var a = await Account(http, db, ct);
                if (a is null) return Results.Unauthorized();
                return Results.Ok(new RedirectDto(await billing.CheckoutAsync(a, req.Plan, req.Quantity, ct)));
            })
            .RequireAuthorization()
            .WithSummary("Cria uma sessão do Stripe Checkout")
            .WithDescription("O plano Ultimate inclui 7 dias grátis na primeira assinatura da conta. Redirecione o usuário para `url`.");

        g.MapPost("/change", async (CheckoutRequest req, HttpContext http, WaGateDbContext db, BillingService billing, CancellationToken ct) =>
            {
                var a = await Account(http, db, ct);
                if (a is null) return Results.Unauthorized();
                await billing.ChangeAsync(a, req.Plan, req.Quantity, ct);
                return Results.Ok(await billing.GetAsync(a, ct));
            })
            .RequireAuthorization()
            .WithSummary("Troca plano ou quantidade de instâncias (cobrança proporcional)");

        g.MapPost("/portal", async (HttpContext http, WaGateDbContext db, BillingService billing, CancellationToken ct) =>
            {
                var a = await Account(http, db, ct);
                if (a is null) return Results.Unauthorized();
                return Results.Ok(new RedirectDto(await billing.PortalAsync(a, ct)));
            })
            .RequireAuthorization()
            .WithSummary("Abre o portal do cliente do Stripe (cartão, faturas, cancelamento)");

        g.MapPost("/sync", async (HttpContext http, WaGateDbContext db, BillingService billing, CancellationToken ct, string? sessionId = null) =>
            {
                var a = await Account(http, db, ct);
                if (a is null) return Results.Unauthorized();
                await billing.SyncAsync(a, sessionId, ct);
                return Results.Ok(await billing.GetAsync(a, ct));
            })
            .RequireAuthorization()
            .WithSummary("Atualiza a assinatura consultando o Stripe (usado no retorno do Checkout)");

        g.MapPost("/stripe/webhook", StripeWebhook)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithSummary("Webhook do Stripe")
            .WithDescription("Valida o header Stripe-Signature com Stripe:WebhookSecret. Eventos: checkout.session.completed, customer.subscription.*, invoice.paid, invoice.payment_failed.");
    }

    private static async Task<Account?> Account(HttpContext http, WaGateDbContext db, CancellationToken ct) =>
        await db.Accounts.FindAsync([http.User.GetAccountId()], ct);

    private static async Task<IResult> StripeWebhook(
        HttpRequest request, WaGateDbContext db, BillingService billing, IOptions<StripeOptions> options,
        ILoggerFactory logs, CancellationToken ct)
    {
        var logger = logs.CreateLogger("StripeWebhook");
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        if (!StripeClient.VerifySignature(payload, request.Headers["Stripe-Signature"], options.Value.WebhookSecret, TimeSpan.FromMinutes(5)))
        {
            logger.LogWarning("Webhook do Stripe com assinatura inválida");
            return Results.BadRequest();
        }

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var type = root.GetProperty("type").GetString()!;

        if (await db.StripeEvents.AnyAsync(e => e.Id == id, ct)) return Results.Ok(new { duplicate = true });

        var obj = root.GetProperty("data").GetProperty("object");
        string? subscriptionId = type switch
        {
            "checkout.session.completed" => Str(obj, "subscription"),
            _ when type.StartsWith("customer.subscription.") => Str(obj, "id"),
            "invoice.paid" or "invoice.payment_failed" or "invoice.payment_succeeded" =>
                Str(obj, "subscription")
                ?? (obj.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object &&
                    parent.TryGetProperty("subscription_details", out var sd) ? Str(sd, "subscription") : null),
            _ => null
        };

        if (subscriptionId is not null)
        {
            // erro aqui → 500 → o Stripe reenvia o evento depois
            await billing.SyncSubscriptionAsync(subscriptionId, ct);
        }

        db.StripeEvents.Add(new StripeEventLog { Id = id, Type = type });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { received = true });

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
