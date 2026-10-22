using Microsoft.EntityFrameworkCore;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Waha;

namespace WaGate.Api.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystem(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/waha", async (IWahaClient waha, WaGateDbContext db, CancellationToken ct) =>
            {
                var d = await waha.DiagnoseAsync(ct);
                var lastEvent = await db.InboundEvents.OrderByDescending(e => e.Id).Select(e => (DateTime?)e.ReceivedAt).FirstOrDefaultAsync(ct);
                return Results.Ok(new
                {
                    d.Reachable, d.ApiKeyValid, d.Version, d.Engine, d.BaseUrl, d.WebhookUrl, d.HmacConfigured, d.Error,
                    lastWebhookReceivedAt = lastEvent
                });
            })
            .RequireAuthorization()
            .WithTags("Sistema")
            .WithSummary("Diagnóstico da conexão com o WAHA")
            .WithDescription("Verifica se o WAHA responde, se a API key é aceita e quando chegou o último webhook dele.");
    }
}
