using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;
using WaGate.Infrastructure.Waha;

namespace WaGate.Api.Endpoints;

/// <summary>Recebe os eventos que o WAHA envia (configurado em cada sessão).</summary>
public static class WahaWebhookEndpoints
{
    public static void MapWahaWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost("/waha/webhook", Receive)
            .AllowAnonymous()
            .DisableAntiforgery()
            .WithTags("WAHA (interno)")
            .WithSummary("Receptor dos webhooks do WAHA")
            .WithDescription("Valida X-Webhook-Hmac (HMAC-SHA512 do corpo) quando Waha:WebhookHmacKey estiver configurado. Uso interno.");
    }

    private static async Task<IResult> Receive(
        HttpRequest request,
        WaGateDbContext db,
        WahaEventProcessor processor,
        IOptions<WahaOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WahaWebhook");

        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        var raw = ms.ToArray();

        var key = options.Value.WebhookHmacKey;
        if (!string.IsNullOrWhiteSpace(key) &&
            !Crypto.VerifyWahaHmac(key, raw, request.Headers["X-Webhook-Hmac"].ToString()))
        {
            logger.LogWarning("Webhook do WAHA rejeitado: HMAC inválido (de {Ip})", request.HttpContext.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }

        WahaEnvelope? env;
        try { env = JsonSerializer.Deserialize<WahaEnvelope>(raw); }
        catch (JsonException) { return Results.BadRequest(); }
        if (env is null || string.IsNullOrEmpty(env.Event)) return Results.BadRequest();

        // idempotência: o WAHA pode reenviar o mesmo evento em caso de timeout
        if (env.Id is not null && await db.InboundEvents.AnyAsync(e => e.WahaEventId == env.Id, ct))
            return Results.Ok(new { duplicate = true });

        var instanceId = await db.Instances.Where(i => i.SessionName == env.Session).Select(i => (Guid?)i.Id).FirstOrDefaultAsync(ct);
        db.InboundEvents.Add(new InboundEvent
        {
            WahaEventId = env.Id,
            Event = env.Event,
            Session = env.Session,
            InstanceId = instanceId,
            Payload = System.Text.Encoding.UTF8.GetString(raw)
        });
        await db.SaveChangesAsync(ct);

        try
        {
            await processor.ProcessAsync(env, ct);
        }
        catch (Exception ex)
        {
            // não devolvemos 5xx para não gerar tempestade de reenvios; o evento bruto fica registrado
            logger.LogError(ex, "Falha ao processar evento {Event} da sessão {Session}", env.Event, env.Session);
        }

        return Results.Ok(new { received = true });
    }
}
