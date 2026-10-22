using Microsoft.EntityFrameworkCore;
using WaGate.Api.Auth;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

public static class WebhookEndpoints
{
    public static void MapWebhooks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Webhooks").RequireAuthorization();

        g.MapGet("/events", () => Results.Ok(WebhookEvents.All))
            .WithSummary("Eventos disponíveis para assinatura");

        g.MapGet("/instances/{id:guid}/webhooks", List).WithSummary("Webhooks da instância");
        g.MapPost("/instances/{id:guid}/webhooks", Create).WithSummary("Cadastra um webhook");
        g.MapPut("/webhooks/{webhookId:guid}", Update).WithSummary("Atualiza um webhook");
        g.MapDelete("/webhooks/{webhookId:guid}", Delete).WithSummary("Remove um webhook");
        g.MapPost("/webhooks/{webhookId:guid}/rotate-secret", RotateSecret).WithSummary("Gera novo segredo de assinatura");
        g.MapPost("/webhooks/{webhookId:guid}/test", Test)
            .WithSummary("Envia um evento webhook.test agora")
            .WithDescription("Executa a entrega de forma síncrona e devolve o resultado (status HTTP, tempo, resposta).");

        var d = app.MapGroup("/api/deliveries").WithTags("Entregas").RequireAuthorization();
        d.MapGet("/", ListDeliveries).WithSummary("Log de entregas de webhook (paginado)");
        d.MapGet("/{deliveryId:guid}", GetDelivery).WithSummary("Detalhe da entrega com payload e resposta");
        d.MapPost("/{deliveryId:guid}/retry", Retry).WithSummary("Reenfileira uma entrega");
    }

    private static List<string> NormalizeEvents(List<string>? events)
    {
        var list = (events ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0 || list.Contains("*")) return ["*"];
        var invalid = list.Where(e => !e.EndsWith(".*") && !WebhookEvents.All.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (invalid.Count > 0) throw new ArgumentException($"Evento(s) desconhecido(s): {string.Join(", ", invalid)}");
        return list;
    }

    private static async Task<Webhook?> FindHook(Guid webhookId, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        return await db.Webhooks.Include(w => w.Instance)
            .FirstOrDefaultAsync(w => w.Id == webhookId && w.Instance!.AccountId == accountId, ct);
    }

    private static async Task<IResult> List(Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        if (await InstanceEndpoints.Find(id, http, db, ct) is null) return Results.NotFound();
        var hooks = await db.Webhooks.AsNoTracking().Where(w => w.InstanceId == id).OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return Results.Ok(hooks.Select(h => h.ToDto()).ToList());
    }

    private static async Task<IResult> Create(Guid id, WebhookRequest req, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        if (await InstanceEndpoints.Find(id, http, db, ct) is null) return Results.NotFound();
        if (await db.Webhooks.CountAsync(w => w.InstanceId == id, ct) >= 10)
            return Results.Problem(statusCode: 403, title: "Máximo de 10 webhooks por instância");

        var hook = new Webhook
        {
            InstanceId = id,
            Url = req.Url.Trim(),
            Events = string.Join(',', NormalizeEvents(req.Events)),
            IsActive = req.IsActive,
            Description = req.Description,
            Secret = Crypto.NewSecret()
        };
        db.Webhooks.Add(hook);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/webhooks/{hook.Id}", hook.ToDto());
    }

    private static async Task<IResult> Update(Guid webhookId, WebhookRequest req, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var hook = await FindHook(webhookId, http, db, ct);
        if (hook is null) return Results.NotFound();
        hook.Url = req.Url.Trim();
        hook.Events = string.Join(',', NormalizeEvents(req.Events));
        hook.IsActive = req.IsActive;
        hook.Description = req.Description;
        await db.SaveChangesAsync(ct);
        return Results.Ok(hook.ToDto());
    }

    private static async Task<IResult> Delete(Guid webhookId, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var hook = await FindHook(webhookId, http, db, ct);
        if (hook is null) return Results.NotFound();
        db.Webhooks.Remove(hook);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RotateSecret(Guid webhookId, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var hook = await FindHook(webhookId, http, db, ct);
        if (hook is null) return Results.NotFound();
        hook.Secret = Crypto.NewSecret();
        await db.SaveChangesAsync(ct);
        return Results.Ok(hook.ToDto());
    }

    private static async Task<IResult> Test(Guid webhookId, HttpContext http, WaGateDbContext db, WebhookDispatcher dispatcher, CancellationToken ct)
    {
        var hook = await FindHook(webhookId, http, db, ct);
        if (hook is null) return Results.NotFound();

        var delivery = EventPublisher.Build(hook.Instance!, hook, WebhookEvents.Test, new
        {
            message = "Webhook de teste do WaGate",
            webhookId = hook.Id,
            sentAt = DateTime.UtcNow
        });
        delivery.MaxAttempts = 1;
        delivery.LockedUntil = DateTime.UtcNow.AddMinutes(1); // impede o despachante de pegar em paralelo
        db.WebhookDeliveries.Add(delivery);
        await db.SaveChangesAsync(ct);

        await dispatcher.DeliverAsync(db, delivery.Id, ct);
        await db.Entry(delivery).ReloadAsync(ct);
        delivery.Webhook = hook;
        return Results.Ok(new DeliveryDetailDto(delivery.ToDto(), delivery.Payload, delivery.LastResponse));
    }

    private static async Task<IResult> ListDeliveries(
        HttpContext http, WaGateDbContext db, CancellationToken ct,
        Guid? instanceId = null, Guid? webhookId = null, string? status = null, string? @event = null,
        int page = 1, int pageSize = 50)
    {
        var accountId = http.User.GetAccountId();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var q = db.WebhookDeliveries.AsNoTracking().Include(d => d.Webhook)
            .Where(d => d.Webhook!.Instance!.AccountId == accountId);
        if (instanceId is not null) q = q.Where(d => d.InstanceId == instanceId);
        if (webhookId is not null) q = q.Where(d => d.WebhookId == webhookId);
        if (Enum.TryParse<DeliveryStatus>(status, true, out var st)) q = q.Where(d => d.Status == st);
        if (!string.IsNullOrWhiteSpace(@event)) q = q.Where(d => d.Event == @event);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(d => d.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<DeliveryDto>(items.Select(d => d.ToDto()).ToList(), page, pageSize, total));
    }

    private static async Task<IResult> GetDelivery(Guid deliveryId, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        var d = await db.WebhookDeliveries.AsNoTracking().Include(x => x.Webhook)
            .FirstOrDefaultAsync(x => x.Id == deliveryId && x.Webhook!.Instance!.AccountId == accountId, ct);
        return d is null ? Results.NotFound() : Results.Ok(new DeliveryDetailDto(d.ToDto(), d.Payload, d.LastResponse));
    }

    private static async Task<IResult> Retry(Guid deliveryId, HttpContext http, WaGateDbContext db, DeliverySignal signal, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        var d = await db.WebhookDeliveries.Include(x => x.Webhook)
            .FirstOrDefaultAsync(x => x.Id == deliveryId && x.Webhook!.Instance!.AccountId == accountId, ct);
        if (d is null) return Results.NotFound();

        d.Status = DeliveryStatus.Pending;
        d.NextAttemptAt = DateTime.UtcNow;
        d.LockedUntil = null;
        d.MaxAttempts = Math.Max(d.MaxAttempts, d.Attempts + 1);
        await db.SaveChangesAsync(ct);
        signal.Notify();
        return Results.Accepted(value: d.ToDto());
    }
}
