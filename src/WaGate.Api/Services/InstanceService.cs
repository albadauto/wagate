using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;
using WaGate.Infrastructure.Waha;
using WaGate.Shared;

namespace WaGate.Api.Services;

public sealed class InstanceLimitException(string message) : Exception(message);

public sealed class InstanceService(
    WaGateDbContext db,
    IWahaClient waha,
    WahaEventProcessor processor,
    BillingService billing,
    ILogger<InstanceService> logger)
{
    public async Task<Instance> CreateAsync(Guid accountId, CreateInstanceRequest req, CancellationToken ct)
    {
        var account = await db.Accounts.FirstAsync(a => a.Id == accountId, ct);
        var count = await db.Instances.CountAsync(i => i.AccountId == accountId, ct);
        var limit = billing.InstanceLimit(account);
        if (billing.Enabled && !account.HasUsableSubscription)
            throw new BillingException(402, "Escolha um plano para criar instâncias. O Ultimate tem 7 dias grátis.");
        if (count >= limit)
            throw new InstanceLimitException(billing.Enabled
                ? $"Seu plano permite {limit} instância(s). Aumente a quantidade em Plano e cobrança."
                : $"Limite de {limit} instâncias atingido para esta conta.");

        var instance = new Instance
        {
            AccountId = accountId,
            Name = req.Name.Trim(),
            Token = Crypto.NewToken(32),
            AutoReadMessages = req.AutoReadMessages,
            RejectCalls = req.RejectCalls
        };
        instance.SessionName = "wg_" + instance.Id.ToString("N")[..16];
        db.Instances.Add(instance);
        await db.SaveChangesAsync(ct);

        if (req.Start)
        {
            try { await StartAsync(instance, ct); }
            catch (Exception ex) when (ex is WahaApiException or HttpRequestException)
            {
                logger.LogWarning(ex, "Instância {Id} criada, mas o WAHA não respondeu ao iniciar a sessão", instance.Id);
                instance.Status = InstanceStatus.Failed;
                instance.StatusChangedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        return instance;
    }

    /// <summary>Cria (ou reconfigura) a sessão no WAHA e inicia.</summary>
    public async Task StartAsync(Instance instance, CancellationToken ct)
    {
        var session = await waha.UpsertSessionAsync(instance.SessionName, instance.Id, start: true, ct);
        await processor.ApplyStatusAsync(instance, DependencyInjection.MapWahaStatus(session.Status), session.Me, ct);
    }

    public async Task StopAsync(Instance instance, CancellationToken ct)
    {
        await waha.StopAsync(instance.SessionName, ct);
        await processor.ApplyStatusAsync(instance, InstanceStatus.Stopped, null, ct);
    }

    public async Task RestartAsync(Instance instance, CancellationToken ct)
    {
        await EnsureSessionExists(instance, ct);
        await waha.RestartAsync(instance.SessionName, ct);
        await processor.ApplyStatusAsync(instance, InstanceStatus.Starting, null, ct);
    }

    public async Task LogoutAsync(Instance instance, CancellationToken ct)
    {
        await waha.LogoutAsync(instance.SessionName, ct);
        instance.PhoneNumber = null;
        instance.PushName = null;
        await processor.ApplyStatusAsync(instance, InstanceStatus.Stopped, null, ct);
        // após logout a sessão fica parada; reinicia para já exibir um novo QR
        try { await StartAsync(instance, ct); } catch (WahaApiException) { }
    }

    public async Task DeleteAsync(Instance instance, CancellationToken ct)
    {
        try { await waha.DeleteAsync(instance.SessionName, ct); }
        catch (Exception ex) when (ex is WahaApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Não foi possível remover a sessão {Session} do WAHA", instance.SessionName);
        }
        db.Instances.Remove(instance);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Consulta o WAHA e atualiza o status local.</summary>
    public async Task SyncAsync(Instance instance, CancellationToken ct)
    {
        var s = await waha.GetSessionAsync(instance.SessionName, ct);
        if (s is null)
        {
            await processor.ApplyStatusAsync(instance, InstanceStatus.Stopped, null, ct);
            return;
        }
        await processor.ApplyStatusAsync(instance, DependencyInjection.MapWahaStatus(s.Status), s.Me, ct);
    }

    public async Task<QrCodeDto> GetQrAsync(Instance instance, CancellationToken ct)
    {
        await SyncAsync(instance, ct);
        var status = Mapping.StatusName(instance.Status);
        return instance.Status switch
        {
            InstanceStatus.Working => new QrCodeDto(status, null, null, "Instância já conectada."),
            InstanceStatus.ScanQrCode => await FetchQr(instance, status, ct),
            InstanceStatus.Starting => new QrCodeDto(status, null, null, "Sessão iniciando, o QR Code aparece em instantes."),
            _ => new QrCodeDto(status, null, null, "Sessão parada. Inicie a instância para gerar o QR Code.")
        };
    }

    private async Task<QrCodeDto> FetchQr(Instance instance, string status, CancellationToken ct)
    {
        var qr = await waha.GetQrAsync(instance.SessionName, ct);
        if (qr?.Data is null) return new QrCodeDto(status, null, qr?.Value, "QR Code ainda não disponível.");
        return new QrCodeDto(status, $"data:{qr.Mimetype ?? "image/png"};base64,{qr.Data}", qr.Value, null);
    }

    private async Task EnsureSessionExists(Instance instance, CancellationToken ct)
    {
        if (await waha.GetSessionAsync(instance.SessionName, ct) is null)
            await waha.UpsertSessionAsync(instance.SessionName, instance.Id, start: false, ct);
    }
}
