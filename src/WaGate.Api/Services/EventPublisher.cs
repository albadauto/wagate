using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure.Data;

namespace WaGate.Api.Services;

/// <summary>Sinal para acordar o despachante assim que houver entregas novas.</summary>
public sealed class DeliverySignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public void Notify() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _channel.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>Grava no outbox uma entrega por webhook ativo que assina o evento.</summary>
public sealed class EventPublisher(WaGateDbContext db, DeliverySignal signal, ILogger<EventPublisher> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public async Task<int> PublishAsync(Instance instance, string evt, object data, CancellationToken ct = default)
    {
        var hooks = await db.Webhooks.AsNoTracking()
            .Where(w => w.InstanceId == instance.Id && w.IsActive)
            .ToListAsync(ct);

        var targets = hooks.Where(h => h.Accepts(evt)).ToList();
        if (targets.Count == 0) return 0;

        foreach (var hook in targets)
            db.WebhookDeliveries.Add(Build(instance, hook, evt, data));

        await db.SaveChangesAsync(ct);
        signal.Notify();
        logger.LogDebug("Evento {Event} da instância {Instance} enfileirado para {Count} webhook(s)", evt, instance.Id, targets.Count);
        return targets.Count;
    }

    public static WebhookDelivery Build(Instance instance, Webhook hook, string evt, object data)
    {
        var delivery = new WebhookDelivery
        {
            WebhookId = hook.Id,
            InstanceId = instance.Id,
            Event = evt,
            NextAttemptAt = DateTime.UtcNow
        };
        var envelope = new
        {
            id = delivery.Id,
            @event = evt,
            instanceId = instance.Id,
            instanceName = instance.Name,
            connectedPhone = instance.PhoneNumber,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            data
        };
        delivery.Payload = JsonSerializer.Serialize(envelope, Json);
        return delivery;
    }
}
