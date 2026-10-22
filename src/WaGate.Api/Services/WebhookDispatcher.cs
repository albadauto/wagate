using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;

namespace WaGate.Api.Services;

/// <summary>
/// Processa o outbox de webhooks. Seguro para várias réplicas: cada entrega é "reivindicada"
/// com um UPDATE condicional em LockedUntil antes do envio.
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpFactory,
    DeliverySignal signal,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 25;
    private const int Parallelism = 8;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(2);

    public const string HeaderEvent = "X-WaGate-Event";
    public const string HeaderDelivery = "X-WaGate-Delivery";
    public const string HeaderTimestamp = "X-WaGate-Timestamp";
    public const string HeaderSignature = "X-WaGate-Signature";
    public const string HeaderAttempt = "X-WaGate-Attempt";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try { processed = await ProcessBatchAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Falha no ciclo do despachante de webhooks"); }

            if (processed < BatchSize)
                await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        List<Guid> ids;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WaGateDbContext>();
            var now = DateTime.UtcNow;
            var candidates = await db.WebhookDeliveries.AsNoTracking()
                .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed)
                            && d.NextAttemptAt <= now
                            && (d.LockedUntil == null || d.LockedUntil < now))
                .OrderBy(d => d.NextAttemptAt)
                .Select(d => d.Id)
                .Take(BatchSize)
                .ToListAsync(ct);

            ids = [];
            foreach (var id in candidates)
            {
                var lockUntil = DateTime.UtcNow.Add(LockFor);
                var claimed = await db.WebhookDeliveries
                    .Where(d => d.Id == id && (d.LockedUntil == null || d.LockedUntil < now))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntil, lockUntil), ct);
                if (claimed == 1) ids.Add(id);
            }
        }

        if (ids.Count == 0) return 0;

        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (id, token) =>
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<WaGateDbContext>();
                await DeliverAsync(db, id, token);
            });

        return ids.Count;
    }

    public async Task DeliverAsync(WaGateDbContext db, Guid deliveryId, CancellationToken ct)
    {
        var d = await db.WebhookDeliveries.Include(x => x.Webhook).FirstOrDefaultAsync(x => x.Id == deliveryId, ct);
        if (d is null) return;

        if (d.Webhook is null || !d.Webhook.IsActive)
        {
            d.Status = DeliveryStatus.Dead;
            d.LastError = "Webhook removido ou desativado";
            d.LockedUntil = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        d.Attempts++;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Crypto.SignWebhook(d.Webhook.Secret, ts, d.Payload);

        using var req = new HttpRequestMessage(HttpMethod.Post, d.Webhook.Url)
        {
            Content = new StringContent(d.Payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Add(HeaderEvent, d.Event);
        req.Headers.Add(HeaderDelivery, d.Id.ToString());
        req.Headers.Add(HeaderTimestamp, ts.ToString());
        req.Headers.Add(HeaderSignature, "sha256=" + signature);
        req.Headers.Add(HeaderAttempt, d.Attempts.ToString());

        var sw = Stopwatch.StartNew();
        try
        {
            var http = httpFactory.CreateClient("webhooks");
            using var res = await http.SendAsync(req, ct);
            sw.Stop();
            var body = await SafeRead(res, ct);
            d.LastStatusCode = (int)res.StatusCode;
            d.LastResponse = body;
            d.LastDurationMs = sw.ElapsedMilliseconds;

            if (res.IsSuccessStatusCode)
            {
                d.Status = DeliveryStatus.Succeeded;
                d.DeliveredAt = DateTime.UtcNow;
                d.LastError = null;
            }
            else
            {
                Fail(d, $"HTTP {(int)res.StatusCode} {res.ReasonPhrase}", RetryPolicy.IsPermanentFailure((int)res.StatusCode));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            sw.Stop();
            d.LastDurationMs = sw.ElapsedMilliseconds;
            d.LastStatusCode = null;
            Fail(d, ex is TaskCanceledException ? "Timeout ao chamar o webhook" : ex.Message, permanent: false);
        }

        d.LockedUntil = null;
        await db.SaveChangesAsync(CancellationToken.None);

        if (d.Status == DeliveryStatus.Succeeded)
            logger.LogInformation("Webhook {Event} entregue a {Url} ({Ms} ms)", d.Event, d.Webhook.Url, d.LastDurationMs);
        else
            logger.LogWarning("Webhook {Event} para {Url} falhou (tentativa {Attempt}/{Max}): {Error}",
                d.Event, d.Webhook.Url, d.Attempts, d.MaxAttempts, d.LastError);
    }

    private static void Fail(WebhookDelivery d, string error, bool permanent)
    {
        d.LastError = error.Length > 1000 ? error[..1000] : error;
        if (permanent || d.Attempts >= d.MaxAttempts)
        {
            d.Status = DeliveryStatus.Dead;
        }
        else
        {
            d.Status = DeliveryStatus.Failed;
            d.NextAttemptAt = DateTime.UtcNow.Add(RetryPolicy.Delay(d.Attempts));
        }
    }

    private static async Task<string?> SafeRead(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var s = await res.Content.ReadAsStringAsync(ct);
            return s.Length > 4000 ? s[..4000] : s;
        }
        catch { return null; }
    }
}
