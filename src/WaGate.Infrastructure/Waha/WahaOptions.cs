namespace WaGate.Infrastructure.Waha;

public sealed class WahaOptions
{
    public const string Section = "Waha";

    /// <summary>URL base do WAHA (ex.: http://localhost:3000).</summary>
    public string BaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Valor de WAHA_API_KEY (enviado em X-Api-Key).</summary>
    public string? ApiKey { get; set; }

    /// <summary>URL pública/rede-interna pela qual o WAHA alcança esta API (POST /waha/webhook).</summary>
    public string WebhookUrl { get; set; } = "http://localhost:5080/waha/webhook";

    /// <summary>Chave HMAC configurada nas sessões; usada para validar X-Webhook-Hmac.</summary>
    public string WebhookHmacKey { get; set; } = "";

    /// <summary>Eventos do WAHA assinados por cada sessão.</summary>
    public string[] Events { get; set; } =
    [
        "session.status", "message.any", "message.ack", "message.ack.group", "message.reaction",
        "message.revoked", "call.received", "group.v2.join", "group.v2.leave", "presence.update"
    ];

    public int TimeoutSeconds { get; set; } = 60;
}
