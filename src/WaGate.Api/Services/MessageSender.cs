using System.Text.Json;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Waha;
using WaGate.Infrastructure.Whatsapp;
using WaGate.Shared;

namespace WaGate.Api.Services;

public sealed class InstanceNotConnectedException(string status)
    : Exception($"A instância não está conectada (status: {status}). Leia o QR Code antes de enviar mensagens.")
{
    public string Status { get; } = status;
}

/// <summary>Envia mensagens via WAHA, registra no histórico e devolve o resultado no formato Z-API.</summary>
public sealed class MessageSender(WaGateDbContext db, IWahaClient waha, ILogger<MessageSender> logger)
{
    public Task<SendResultDto> TextAsync(Instance i, SendTextRequest r, CancellationToken ct) =>
        Run(i, r, MessageType.Text, r.Message, null, null,
            chat => waha.SendTextAsync(i.SessionName, chat, r.Message, r.MessageId, r.LinkPreview, r.Mentions, ct), ct);

    public Task<SendResultDto> ImageAsync(Instance i, SendImageRequest r, CancellationToken ct)
    {
        var file = Media.ToFile(r, "image/jpeg", "image.jpg");
        return Run(i, r, MessageType.Image, r.Caption, r.Url, file.Filename,
            chat => waha.SendImageAsync(i.SessionName, chat, file, r.Caption, r.MessageId, ct), ct);
    }

    public Task<SendResultDto> DocumentAsync(Instance i, SendDocumentRequest r, CancellationToken ct)
    {
        var file = Media.ToFile(r, "application/octet-stream", "arquivo");
        return Run(i, r, MessageType.Document, r.Caption, r.Url, file.Filename,
            chat => waha.SendFileAsync(i.SessionName, chat, file, r.Caption, r.MessageId, ct), ct);
    }

    public Task<SendResultDto> AudioAsync(Instance i, SendAudioRequest r, CancellationToken ct)
    {
        var file = Media.ToFile(r, "audio/ogg; codecs=opus", "audio.ogg");
        return Run(i, r, MessageType.Audio, null, r.Url, file.Filename,
            chat => waha.SendVoiceAsync(i.SessionName, chat, file, r.Convert, r.MessageId, ct), ct);
    }

    public Task<SendResultDto> VideoAsync(Instance i, SendVideoRequest r, CancellationToken ct)
    {
        var file = Media.ToFile(r, "video/mp4", "video.mp4");
        return Run(i, r, MessageType.Video, r.Caption, r.Url, file.Filename,
            chat => waha.SendVideoAsync(i.SessionName, chat, file, r.Caption, r.Convert, r.AsNote, r.MessageId, ct), ct);
    }

    public Task<SendResultDto> LocationAsync(Instance i, SendLocationRequest r, CancellationToken ct)
    {
        var title = string.Join(" - ", new[] { r.Title, r.Address }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return Run(i, r, MessageType.Location, $"{r.Latitude},{r.Longitude} {title}".Trim(), null, null,
            chat => waha.SendLocationAsync(i.SessionName, chat, r.Latitude, r.Longitude, string.IsNullOrEmpty(title) ? null : title, r.MessageId, ct), ct);
    }

    public Task<SendResultDto> ContactAsync(Instance i, SendContactRequest r, CancellationToken ct) =>
        Run(i, r, MessageType.Contact, $"{r.ContactName} ({r.ContactPhone})", null, null,
            chat => waha.SendContactAsync(i.SessionName, chat, r.ContactName, r.ContactPhone, r.Organization, r.MessageId, ct), ct);

    public async Task ReactAsync(Instance i, SendReactionRequest r, CancellationToken ct)
    {
        EnsureConnected(i);
        await waha.ReactAsync(i.SessionName, r.MessageId, r.Reaction, ct);
    }

    private async Task<SendResultDto> Run(
        Instance instance, SendBase req, MessageType type, string? body, string? mediaUrl, string? fileName,
        Func<string, Task<JsonElement>> send, CancellationToken ct)
    {
        EnsureConnected(instance);
        var chatId = ChatIds.ToChatId(req.Phone);

        if (req.DelayTyping is > 0)
        {
            try
            {
                await waha.TypingAsync(instance.SessionName, chatId, true, ct);
                await Task.Delay(TimeSpan.FromSeconds(req.DelayTyping.Value), ct);
                await waha.TypingAsync(instance.SessionName, chatId, false, ct);
            }
            catch (WahaApiException ex) { logger.LogDebug(ex, "Falha ao simular digitação"); }
        }

        var msg = new Message
        {
            InstanceId = instance.Id,
            ChatId = chatId,
            Phone = ChatIds.ToPhone(chatId),
            Direction = MessageDirection.Outbound,
            Type = type,
            Body = body is { Length: > 4000 } ? body[..4000] : body,
            MediaUrl = mediaUrl,
            FileName = fileName,
            IsGroup = ChatIds.IsGroup(chatId),
            Status = MessageStatus.Pending
        };
        db.Messages.Add(msg);

        try
        {
            var res = await send(chatId);
            msg.ExternalId = WahaClient.ExtractMessageId(res);
            msg.Status = MessageStatus.Sent;
            msg.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return new SendResultDto(msg.Id.ToString(), msg.ExternalId, msg.ExternalId ?? msg.Id.ToString());
        }
        catch (WahaApiException ex)
        {
            msg.Status = MessageStatus.Failed;
            msg.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static void EnsureConnected(Instance i)
    {
        if (i.Status != InstanceStatus.Working)
            throw new InstanceNotConnectedException(Mapping.StatusName(i.Status));
    }
}

public static class Media
{
    private static readonly Dictionary<string, string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp", [".gif"] = "image/gif",
        [".pdf"] = "application/pdf", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".csv"] = "text/csv", [".txt"] = "text/plain", [".zip"] = "application/zip",
        [".mp3"] = "audio/mpeg", [".ogg"] = "audio/ogg", [".opus"] = "audio/ogg", [".m4a"] = "audio/mp4", [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4", [".mov"] = "video/quicktime", [".3gp"] = "video/3gpp"
    };

    /// <summary>Monta o objeto file do WAHA a partir de URL ou base64 (aceita prefixo data:mime;base64,).</summary>
    public static WahaFile ToFile(SendMediaBase r, string defaultMime, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(r.Url) && string.IsNullOrWhiteSpace(r.Base64))
            throw new ArgumentException("Informe 'url' ou 'base64' da mídia.");

        string? mime = r.MimeType;
        string? data = null;

        if (!string.IsNullOrWhiteSpace(r.Base64))
        {
            data = r.Base64.Trim();
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = data.IndexOf(',');
                if (comma < 0) throw new ArgumentException("base64 em formato data URI inválido.");
                var header = data[5..comma];               // image/png;base64
                mime ??= header.Split(';')[0];
                data = data[(comma + 1)..];
            }
        }

        var fileName = r.FileName;
        if (fileName is null && r.Url is not null && Uri.TryCreate(r.Url, UriKind.Absolute, out var uri))
        {
            var last = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrEmpty(last) && last.Contains('.')) fileName = last;
        }
        fileName ??= defaultName;

        if (mime is null && Mimes.TryGetValue(Path.GetExtension(fileName), out var guessed)) mime = guessed;
        mime ??= defaultMime;

        return new WahaFile(mime, fileName, data is null ? r.Url : null, data);
    }
}
