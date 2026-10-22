using WaGate.Domain;
using WaGate.Shared;

namespace WaGate.Api.Services;

public static class Mapping
{
    public static AccountDto ToDto(this Account a) => new(a.Id, a.Name, a.Email, a.ClientToken, a.ClientTokenEnabled, a.MaxInstances, a.CreatedAt,
        a.Phone, a.Document is null ? null : BrDocs.FormatDocument(a.Document), a.CompanyName);

    public static InstanceDto ToDto(this Instance i, int webhookCount = 0) => new(
        i.Id, i.Name, i.SessionName, i.Token, StatusName(i.Status), i.PhoneNumber, i.PushName,
        i.AutoReadMessages, i.RejectCalls, i.CreatedAt, i.StatusChangedAt, i.ConnectedAt, webhookCount);

    public static WebhookDto ToDto(this Webhook w) => new(w.Id, w.InstanceId, w.Url, w.EventList, w.Secret, w.IsActive, w.Description, w.CreatedAt);

    public static MessageDto ToDto(this Message m) => new(
        m.Id, m.InstanceId, m.ExternalId, m.ChatId, m.Phone, m.Direction.ToString(), m.Type.ToString(),
        m.Body, m.MediaUrl, m.FileName, m.SenderName, m.IsGroup, m.Status.ToString(), m.Error, m.CreatedAt, m.UpdatedAt);

    public static DeliveryDto ToDto(this WebhookDelivery d) => new(
        d.Id, d.WebhookId, d.Webhook?.Url ?? "", d.InstanceId, d.Event, d.Status.ToString(), d.Attempts, d.MaxAttempts,
        d.NextAttemptAt, d.LastStatusCode, d.LastError, d.LastDurationMs, d.CreatedAt, d.DeliveredAt);

    /// <summary>Nome do status no padrão do WAHA (SCAN_QR_CODE, WORKING...).</summary>
    public static string StatusName(InstanceStatus s) => s switch
    {
        InstanceStatus.Created => "CREATED",
        InstanceStatus.Stopped => "STOPPED",
        InstanceStatus.Starting => "STARTING",
        InstanceStatus.ScanQrCode => "SCAN_QR_CODE",
        InstanceStatus.Working => "WORKING",
        InstanceStatus.Failed => "FAILED",
        _ => "UNKNOWN"
    };
}
