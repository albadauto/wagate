using Microsoft.EntityFrameworkCore;
using WaGate.Api.Auth;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboard(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", Get)
            .RequireAuthorization()
            .WithTags("Painel")
            .WithSummary("Indicadores da conta (instâncias, mensagens, entregas)");
    }

    private static async Task<IResult> Get(HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        var instanceIds = await db.Instances.Where(i => i.AccountId == accountId).Select(i => i.Id).ToListAsync(ct);
        var connected = await db.Instances.CountAsync(i => i.AccountId == accountId && i.Status == InstanceStatus.Working, ct);

        var today = DateTime.UtcNow.Date;
        var since7 = today.AddDays(-6);
        var since24h = DateTime.UtcNow.AddHours(-24);

        var msgs = db.Messages.AsNoTracking().Where(m => instanceIds.Contains(m.InstanceId));
        var daily = await msgs.Where(m => m.CreatedAt >= since7)
            .GroupBy(m => new { m.CreatedAt.Date, m.Direction })
            .Select(g => new { g.Key.Date, g.Key.Direction, Count = g.Count() })
            .ToListAsync(ct);

        var last7 = Enumerable.Range(0, 7).Select(i => since7.AddDays(i)).Select(d => new DailyCount(
            DateOnly.FromDateTime(d),
            daily.Where(x => x.Date == d && x.Direction == MessageDirection.Inbound).Sum(x => x.Count),
            daily.Where(x => x.Date == d && x.Direction == MessageDirection.Outbound).Sum(x => x.Count))).ToList();

        var todayIn = last7[^1].Inbound;
        var todayOut = last7[^1].Outbound;

        var deliveries = db.WebhookDeliveries.AsNoTracking().Where(d => instanceIds.Contains(d.InstanceId));
        var pending = await deliveries.CountAsync(d => d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed, ct);
        var recent = await deliveries.Where(d => d.CreatedAt >= since24h)
            .GroupBy(d => d.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var ok = recent.Where(r => r.Key == DeliveryStatus.Succeeded).Sum(r => r.Count);
        var dead = recent.Where(r => r.Key == DeliveryStatus.Dead).Sum(r => r.Count);
        var failedNow = recent.Where(r => r.Key == DeliveryStatus.Failed).Sum(r => r.Count);
        var done = ok + dead;
        var rate = done == 0 ? 100 : Math.Round(ok * 100.0 / done, 1);

        return Results.Ok(new DashboardDto(
            instanceIds.Count, connected, todayIn + todayOut, todayIn, todayOut,
            pending, dead + failedNow, rate, last7));
    }
}
