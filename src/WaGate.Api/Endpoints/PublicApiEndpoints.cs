using WaGate.Api.Auth;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Waha;
using WaGate.Infrastructure.Whatsapp;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

/// <summary>
/// API pública no estilo Z-API: /instances/{instanceId}/token/{token}/{ação}, autenticada pelo
/// token da instância na URL + header Client-Token da conta.
/// </summary>
public static class PublicApiEndpoints
{
    public static void MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/instances/{instanceId:guid}/token/{token}")
            .AddEndpointFilter<InstanceTokenFilter>()
            .RequireRateLimiting("instance")
            .WithTags("API de envio");

        // ---- Mensagens ----
        g.MapPost("/send-text", (SendTextRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
                Ok(s.TextAsync(h.GetInstance(), r, ct)))
            .WithSummary("Envia texto")
            .WithDescription("`phone` aceita número com DDI (5511999999999) ou id de grupo (xxxx@g.us). `delayTyping` mostra \"digitando...\" por N segundos antes do envio.");

        g.MapPost("/send-image", (SendImageRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.ImageAsync(h.GetInstance(), r, ct))).WithSummary("Envia imagem (url ou base64)");

        g.MapPost("/send-document", (SendDocumentRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.DocumentAsync(h.GetInstance(), r, ct))).WithSummary("Envia documento/arquivo");

        g.MapPost("/send-audio", (SendAudioRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.AudioAsync(h.GetInstance(), r, ct))).WithSummary("Envia áudio como nota de voz");

        g.MapPost("/send-video", (SendVideoRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.VideoAsync(h.GetInstance(), r, ct))).WithSummary("Envia vídeo");

        g.MapPost("/send-location", (SendLocationRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.LocationAsync(h.GetInstance(), r, ct))).WithSummary("Envia localização");

        g.MapPost("/send-contact", (SendContactRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
            Ok(s.ContactAsync(h.GetInstance(), r, ct))).WithSummary("Envia cartão de contato (vCard)");

        g.MapPost("/send-reaction", async (SendReactionRequest r, HttpContext h, MessageSender s, CancellationToken ct) =>
        {
            await s.ReactAsync(h.GetInstance(), r, ct);
            return Results.Ok(new { value = true });
        }).WithSummary("Reage a uma mensagem (reaction vazio remove)");

        g.MapPost("/read-message", async (ReadMessageRequest r, HttpContext h, IWahaClient waha, CancellationToken ct) =>
        {
            var i = Connected(h);
            await waha.SendSeenAsync(i.SessionName, ChatIds.ToChatId(r.Phone), r.MessageIds, ct);
            return Results.Ok(new { value = true });
        }).WithSummary("Marca o chat/mensagens como lidos");

        g.MapPost("/typing", async (TypingRequest r, HttpContext h, IWahaClient waha, CancellationToken ct) =>
        {
            var i = Connected(h);
            await waha.TypingAsync(i.SessionName, ChatIds.ToChatId(r.Phone), r.Typing, ct);
            return Results.Ok(new { value = true });
        }).WithSummary("Inicia/para o indicador \"digitando...\"");

        g.MapGet("/phone-exists/{phone}", async (string phone, HttpContext h, IWahaClient waha, CancellationToken ct) =>
        {
            var i = Connected(h);
            var r = await waha.CheckExistsAsync(i.SessionName, phone, ct);
            return Results.Ok(new PhoneExistsDto(phone, r.NumberExists, r.ChatId));
        }).WithSummary("Verifica se o número tem WhatsApp");

        // ---- Instância ----
        g.MapGet("/status", async (HttpContext h, InstanceService s, CancellationToken ct) =>
        {
            var i = h.GetInstance();
            try { await s.SyncAsync(i, ct); } catch (WahaApiException) { }
            var connected = i.Status == InstanceStatus.Working;
            return Results.Ok(new InstanceStatusDto(connected, Mapping.StatusName(i.Status), i.PhoneNumber, i.PushName, connected));
        }).WithSummary("Status da conexão");

        g.MapGet("/qr-code", async (HttpContext h, InstanceService s, CancellationToken ct) =>
                Results.Ok(await s.GetQrAsync(h.GetInstance(), ct)))
            .WithSummary("QR Code em base64 (data URI)");

        g.MapGet("/qr-code/image", async (HttpContext h, InstanceService s, CancellationToken ct) =>
        {
            var qr = await s.GetQrAsync(h.GetInstance(), ct);
            if (qr.Image is null) return Results.Problem(statusCode: 409, title: qr.Message ?? "QR indisponível", detail: qr.Status);
            var comma = qr.Image.IndexOf(',');
            var mime = qr.Image[5..qr.Image.IndexOf(';')];
            return Results.File(Convert.FromBase64String(qr.Image[(comma + 1)..]), mime);
        }).WithSummary("QR Code como imagem PNG");

        g.MapPost("/restart", async (HttpContext h, InstanceService s, CancellationToken ct) =>
        {
            await s.RestartAsync(h.GetInstance(), ct);
            return Results.Ok(new { value = true });
        }).WithSummary("Reinicia a sessão");

        g.MapPost("/disconnect", async (HttpContext h, InstanceService s, CancellationToken ct) =>
        {
            await s.LogoutAsync(h.GetInstance(), ct);
            return Results.Ok(new { value = true });
        }).WithSummary("Desconecta o número");
    }

    private static async Task<IResult> Ok(Task<SendResultDto> task) => Results.Ok(await task);

    private static Instance Connected(HttpContext h)
    {
        var i = h.GetInstance();
        if (i.Status != InstanceStatus.Working) throw new InstanceNotConnectedException(Mapping.StatusName(i.Status));
        return i;
    }
}
