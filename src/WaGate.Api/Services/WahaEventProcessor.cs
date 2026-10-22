using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Waha;
using WaGate.Infrastructure.Whatsapp;

namespace WaGate.Api.Services;

/// <summary>Traduz os eventos do WAHA para o modelo do WaGate, persiste e publica nos webhooks dos clientes.</summary>
public sealed class WahaEventProcessor(
    WaGateDbContext db,
    EventPublisher publisher,
    IWahaClient waha,
    ILogger<WahaEventProcessor> logger)
{
    public async Task ProcessAsync(WahaEnvelope env, CancellationToken ct)
    {
        var instance = await db.Instances.FirstOrDefaultAsync(i => i.SessionName == env.Session, ct);
        if (instance is null)
        {
            logger.LogWarning("Evento {Event} recebido para sessão desconhecida {Session}", env.Event, env.Session);
            return;
        }

        var p = env.Payload;
        switch (env.Event)
        {
            case "session.status":
                await OnSessionStatus(instance, p, env.Me, ct);
                break;
            case "message.any":
            case "message":
                await OnMessage(instance, p, ct);
                break;
            case "message.ack":
            case "message.ack.group":
                await OnAck(instance, p, ct);
                break;
            case "message.reaction":
                await publisher.PublishAsync(instance, WebhookEvents.MessageReaction, new
                {
                    messageId = Str(p, "id"),
                    phone = ChatIds.ToPhone(Str(p, "from")),
                    chatId = Str(p, "from"),
                    fromMe = Bool(p, "fromMe"),
                    reaction = p.TryGetProperty("reaction", out var r) ? Str(r, "text") : null,
                    reactedMessageId = p.TryGetProperty("reaction", out var r2) ? Str(r2, "messageId") : null,
                    timestamp = Long(p, "timestamp")
                }, ct);
                break;
            case "message.revoked":
                await publisher.PublishAsync(instance, WebhookEvents.MessageRevoked, new
                {
                    revokedMessageId = Str(p, "revokedMessageId"),
                    after = p.TryGetProperty("after", out var after) ? NormalizeMessage(after) : null
                }, ct);
                break;
            case "call.received":
                await OnCall(instance, p, ct);
                break;
            case "group.v2.join":
                await publisher.PublishAsync(instance, WebhookEvents.GroupJoin, new { raw = p }, ct);
                break;
            case "group.v2.leave":
                await publisher.PublishAsync(instance, WebhookEvents.GroupLeave, new { raw = p }, ct);
                break;
            case "presence.update":
                await publisher.PublishAsync(instance, WebhookEvents.Presence, new { raw = p }, ct);
                break;
            default:
                logger.LogDebug("Evento WAHA {Event} ignorado", env.Event);
                break;
        }
    }

    // ---------------- Status da sessão ----------------

    private async Task OnSessionStatus(Instance instance, JsonElement p, WahaMe? me, CancellationToken ct)
    {
        var newStatus = DependencyInjection.MapWahaStatus(Str(p, "status"));
        if (newStatus == InstanceStatus.Working && me is null)
        {
            try { me = await waha.GetMeAsync(instance.SessionName, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Não foi possível obter /me"); }
        }
        await ApplyStatusAsync(instance, newStatus, me, ct);
    }

    /// <summary>Aplica o status e publica os eventos de transição. Também usado pela sincronização periódica.</summary>
    public async Task ApplyStatusAsync(Instance instance, InstanceStatus newStatus, WahaMe? me, CancellationToken ct)
    {
        var previous = instance.Status;
        if (me?.Id is not null)
        {
            instance.PhoneNumber = ChatIds.ToPhone(me.Id);
            instance.PushName = me.PushName ?? instance.PushName;
        }
        if (previous == newStatus)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        instance.Status = newStatus;
        instance.StatusChangedAt = DateTime.UtcNow;
        if (newStatus == InstanceStatus.Working) instance.ConnectedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var data = new
        {
            status = Mapping.StatusName(newStatus),
            previousStatus = Mapping.StatusName(previous),
            connected = newStatus == InstanceStatus.Working,
            phone = instance.PhoneNumber,
            pushName = instance.PushName
        };
        await publisher.PublishAsync(instance, WebhookEvents.InstanceStatus, data, ct);

        if (newStatus == InstanceStatus.Working)
            await publisher.PublishAsync(instance, WebhookEvents.InstanceConnected, data, ct);
        else if (previous == InstanceStatus.Working)
            await publisher.PublishAsync(instance, WebhookEvents.InstanceDisconnected, data, ct);
    }

    // ---------------- Mensagens ----------------

    private async Task OnMessage(Instance instance, JsonElement p, CancellationToken ct)
    {
        var externalId = MessageId(p);
        var fromMe = Bool(p, "fromMe");
        var chatId = (fromMe ? Str(p, "to") : Str(p, "from")) ?? "";
        if (chatId.EndsWith("@broadcast")) return; // status/stories

        var normalized = NormalizeMessage(p);
        Message? msg = null;
        if (externalId is not null)
        {
            // Envios pela API: o WAHA pode disparar message.any antes de gravarmos o ExternalId.
            var tries = fromMe && Str(p, "source") == "api" ? 4 : 1;
            for (var i = 0; i < tries && msg is null; i++)
            {
                if (i > 0) await Task.Delay(400, ct);
                msg = await db.Messages.FirstOrDefaultAsync(m => m.InstanceId == instance.Id && m.ExternalId == externalId, ct);
            }
        }

        if (msg is null)
        {
            msg = new Message
            {
                InstanceId = instance.Id,
                ExternalId = externalId,
                ChatId = chatId,
                Phone = ChatIds.ToPhone(Str(p, "participant") ?? chatId),
                Direction = fromMe ? MessageDirection.Outbound : MessageDirection.Inbound,
                Type = normalized.Type,
                Body = Truncate(normalized.Text, 4000),
                MediaUrl = normalized.Media?.Url,
                FileName = normalized.Media?.Filename,
                SenderName = normalized.SenderName,
                IsGroup = ChatIds.IsGroup(chatId),
                Status = fromMe ? MessageStatus.Sent : MessageStatus.Received,
                CreatedAt = normalized.Timestamp is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : DateTime.UtcNow
            };
            db.Messages.Add(msg);
        }
        else
        {
            // mensagem enviada pela API: o WAHA confirma via message.any
            msg.MediaUrl ??= normalized.Media?.Url;
            if (msg.Status == MessageStatus.Pending) msg.Status = MessageStatus.Sent;
            msg.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);

        var evt = fromMe ? WebhookEvents.MessageSent : WebhookEvents.MessageReceived;
        await publisher.PublishAsync(instance, evt, normalized.ToPayload(msg.Id, Str(p, "source")), ct);

        if (!fromMe && instance.AutoReadMessages && externalId is not null)
        {
            try { await waha.SendSeenAsync(instance.SessionName, chatId, [externalId], ct); }
            catch (Exception ex) { logger.LogDebug(ex, "Falha ao marcar como lida"); }
        }
    }

    private async Task OnAck(Instance instance, JsonElement p, CancellationToken ct)
    {
        var externalId = MessageId(p);
        var ack = p.TryGetProperty("ack", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
        var status = ack switch
        {
            -1 => MessageStatus.Failed,
            0 => MessageStatus.Pending,
            1 => MessageStatus.Sent,
            2 => MessageStatus.Delivered,
            3 => MessageStatus.Read,
            >= 4 => MessageStatus.Played,
            _ => MessageStatus.Pending
        };

        if (externalId is not null)
        {
            var msg = await db.Messages.FirstOrDefaultAsync(m => m.InstanceId == instance.Id && m.ExternalId == externalId, ct);
            // ack só avança (SENT → DELIVERED → READ → PLAYED), exceto falha
            if (msg is not null && msg.Direction == MessageDirection.Outbound &&
                (status == MessageStatus.Failed || (int)status > (int)msg.Status))
            {
                msg.Status = status;
                msg.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }

        var chat = Bool(p, "fromMe") ? Str(p, "to") : Str(p, "from");
        await publisher.PublishAsync(instance, WebhookEvents.MessageStatus, new
        {
            messageId = externalId,
            chatId = chat,
            phone = ChatIds.ToPhone(Str(p, "participant") ?? chat),
            status = status.ToString().ToUpperInvariant(),
            ack,
            ackName = Str(p, "ackName"),
            fromMe = Bool(p, "fromMe")
        }, ct);
    }

    private async Task OnCall(Instance instance, JsonElement p, CancellationToken ct)
    {
        var from = Str(p, "from");
        var callId = Str(p, "id");
        var rejected = false;
        if (instance.RejectCalls && from is not null && callId is not null)
        {
            try { await waha.RejectCallAsync(instance.SessionName, from, callId, ct); rejected = true; }
            catch (Exception ex) { logger.LogWarning(ex, "Falha ao rejeitar chamada"); }
        }

        await publisher.PublishAsync(instance, WebhookEvents.Call, new
        {
            callId,
            phone = ChatIds.ToPhone(from),
            chatId = from,
            isVideo = Bool(p, "isVideo"),
            isGroup = Bool(p, "isGroup"),
            rejected,
            timestamp = Long(p, "timestamp")
        }, ct);
    }

    // ---------------- Normalização ----------------

    public sealed record NormalizedMedia(string? Url, string? Mimetype, string? Filename);

    public sealed record NormalizedMessage(
        string? MessageId, string ChatId, string? Phone, bool FromMe, bool IsGroup, string? Participant,
        string? SenderName, MessageType Type, string? Text, NormalizedMedia? Media,
        double? Latitude, double? Longitude, string? ReplyTo, long? Timestamp, JsonElement Raw)
    {
        public object ToPayload(Guid id, string? source) => new
        {
            id,
            messageId = MessageId,
            chatId = ChatId,
            phone = Phone,
            fromMe = FromMe,
            fromApi = source == "api",
            isGroup = IsGroup,
            participant = Participant,
            senderName = SenderName,
            type = Type.ToString().ToLowerInvariant(),
            text = Text,
            media = Media,
            location = Latitude is null ? null : new { latitude = Latitude, longitude = Longitude },
            replyTo = ReplyTo,
            timestamp = Timestamp,
            raw = Raw
        };
    }

    public static NormalizedMessage NormalizeMessage(JsonElement p)
    {
        var fromMe = Bool(p, "fromMe");
        var chatId = (fromMe ? Str(p, "to") : Str(p, "from")) ?? "";
        var participant = Str(p, "participant");
        var hasMedia = Bool(p, "hasMedia");

        NormalizedMedia? media = null;
        if (p.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object)
            media = new NormalizedMedia(Str(m, "url"), Str(m, "mimetype"), Str(m, "filename"));

        double? lat = null, lng = null;
        if (p.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
        {
            lat = Dbl(loc, "latitude");
            lng = Dbl(loc, "longitude");
        }

        string? dataType = null;
        string? senderName = null;
        if (p.TryGetProperty("_data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            dataType = Str(data, "type");
            senderName = Str(data, "notifyName") ?? Str(data, "pushName");
            if (lat is null && dataType == "location")
            {
                lat = Dbl(data, "lat");
                lng = Dbl(data, "lng");
            }
        }

        var type = DetectType(hasMedia, media?.Mimetype, dataType, lat is not null, p);

        string? replyTo = null;
        if (p.TryGetProperty("replyTo", out var rt))
            replyTo = rt.ValueKind == JsonValueKind.String ? rt.GetString() : rt.ValueKind == JsonValueKind.Object ? Str(rt, "id") : null;

        return new NormalizedMessage(
            MessageId(p), chatId, ChatIds.ToPhone(participant ?? chatId), fromMe, ChatIds.IsGroup(chatId), participant,
            senderName, type, Str(p, "body"), media, lat, lng, replyTo, Long(p, "timestamp"), p.Clone());
    }

    private static MessageType DetectType(bool hasMedia, string? mime, string? dataType, bool hasLocation, JsonElement p)
    {
        if (hasLocation) return MessageType.Location;
        switch (dataType)
        {
            case "vcard" or "multi_vcard": return MessageType.Contact;
            case "sticker": return MessageType.Sticker;
            case "reaction": return MessageType.Reaction;
            case "ptt" or "audio": return MessageType.Audio;
            case "image": return MessageType.Image;
            case "video": return MessageType.Video;
            case "document": return MessageType.Document;
        }
        if (p.TryGetProperty("vCards", out var vc) && vc.ValueKind == JsonValueKind.Array && vc.GetArrayLength() > 0)
            return MessageType.Contact;
        if (!hasMedia && mime is null) return MessageType.Text;
        if (mime is null) return MessageType.Other;
        if (mime == "image/webp") return MessageType.Sticker;
        if (mime.StartsWith("image/")) return MessageType.Image;
        if (mime.StartsWith("video/")) return MessageType.Video;
        if (mime.StartsWith("audio/")) return MessageType.Audio;
        return MessageType.Document;
    }

    // ---------------- Helpers JSON ----------------

    public static string? MessageId(JsonElement p)
    {
        if (!p.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Object => Str(id, "_serialized") ?? Str(id, "id"),
            _ => null
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? Dbl(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    private static string? Truncate(string? s, int n) => s is null || s.Length <= n ? s : s[..n];
}
