using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Waha;

namespace WaGate.Api.Services;

/// <summary>Reconcilia periodicamente o status das instâncias com o WAHA (cobre webhooks perdidos).</summary>
public sealed class StatusSyncService(IServiceScopeFactory scopes, ILogger<StatusSyncService> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Jobs:StatusSyncSeconds", 60));
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        using var timer = new PeriodicTimer(interval);
        var first = true;
        do
        {
            try { await SyncAllAsync(first, stoppingToken); first = false; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Sincronização de status falhou: {Message}", ex.Message); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAllAsync(bool reconcileConfig, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaGateDbContext>();
        var waha = scope.ServiceProvider.GetRequiredService<IWahaClient>();
        var processor = scope.ServiceProvider.GetRequiredService<WahaEventProcessor>();

        var sessions = (await waha.ListSessionsAsync(ct)).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var instances = await db.Instances.Where(i => i.Status != InstanceStatus.Created).ToListAsync(ct);

        // Na primeira execução: se a URL/eventos/HMAC do webhook mudaram (ex.: troca de Waha:WebhookUrl),
        // reconfigura as sessões existentes para o WAHA voltar a entregar os eventos.
        if (reconcileConfig)
        {
            var opt = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<WahaOptions>>().Value;
            foreach (var instance in instances.Where(i => sessions.ContainsKey(i.SessionName)))
            {
                var s = sessions[instance.SessionName];
                var cfg = s.Config ?? (await waha.GetSessionAsync(instance.SessionName, ct))?.Config;
                if (WahaClient.WebhookConfigMatches(cfg, opt)) continue;
                try
                {
                    logger.LogInformation("Atualizando webhook da sessão {Session} no WAHA", instance.SessionName);
                    await waha.UpsertSessionAsync(instance.SessionName, instance.Id, start: false, ct);
                }
                catch (Exception ex) { logger.LogWarning("Falha ao reconfigurar {Session}: {Message}", instance.SessionName, ex.Message); }
            }
        }

        foreach (var instance in instances)
        {
            var status = sessions.TryGetValue(instance.SessionName, out var s)
                ? DependencyInjection.MapWahaStatus(s.Status)
                : InstanceStatus.Stopped;
            if (status != instance.Status || (status == InstanceStatus.Working && instance.PhoneNumber is null))
                await processor.ApplyStatusAsync(instance, status, s?.Me, ct);
        }
    }
}

/// <summary>Limpeza de dados antigos: eventos brutos e entregas concluídas.</summary>
public sealed class RetentionService(IServiceScopeFactory scopes, ILogger<RetentionService> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaGateDbContext>();
                var eventsCutoff = DateTime.UtcNow.AddDays(-config.GetValue("Retention:InboundEventsDays", 7));
                var deliveriesCutoff = DateTime.UtcNow.AddDays(-config.GetValue("Retention:DeliveriesDays", 30));

                var e = await db.InboundEvents.Where(x => x.ReceivedAt < eventsCutoff).ExecuteDeleteAsync(stoppingToken);
                var d = await db.WebhookDeliveries
                    .Where(x => x.CreatedAt < deliveriesCutoff && (x.Status == DeliveryStatus.Succeeded || x.Status == DeliveryStatus.Dead))
                    .ExecuteDeleteAsync(stoppingToken);
                if (e + d > 0) logger.LogInformation("Retenção: {Events} eventos e {Deliveries} entregas removidos", e, d);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Falha na rotina de retenção"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
