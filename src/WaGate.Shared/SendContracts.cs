using System.ComponentModel.DataAnnotations;

namespace WaGate.Shared;

/// <summary>Base dos envios: telefone com DDI (ex.: 5511999999999) ou id de grupo (xxx@g.us).</summary>
public abstract class SendBase
{
    [Required, StringLength(64)] public string Phone { get; set; } = "";
    /// <summary>Id da mensagem a ser respondida (citação).</summary>
    public string? MessageId { get; set; }
    /// <summary>Atraso em segundos (1 a 15) exibindo "digitando..." antes do envio.</summary>
    [Range(0, 15)] public int? DelayTyping { get; set; }
}

public sealed class SendTextRequest : SendBase
{
    [Required, StringLength(65536, MinimumLength = 1)] public string Message { get; set; } = "";
    public bool LinkPreview { get; set; } = true;
    /// <summary>Em grupos: lista de telefones a mencionar, ou ["all"].</summary>
    public List<string>? Mentions { get; set; }
}

/// <summary>Mídia por URL pública ou base64 (com ou sem prefixo data:).</summary>
public abstract class SendMediaBase : SendBase
{
    public string? Url { get; set; }
    public string? Base64 { get; set; }
    public string? MimeType { get; set; }
    public string? FileName { get; set; }
}

public sealed class SendImageRequest : SendMediaBase
{
    public string? Caption { get; set; }
}

public sealed class SendDocumentRequest : SendMediaBase
{
    public string? Caption { get; set; }
}

public sealed class SendAudioRequest : SendMediaBase
{
    /// <summary>Converte automaticamente para OGG/Opus (nota de voz).</summary>
    public bool Convert { get; set; } = true;
}

public sealed class SendVideoRequest : SendMediaBase
{
    public string? Caption { get; set; }
    public bool Convert { get; set; } = true;
    public bool AsNote { get; set; }
}

public sealed class SendLocationRequest : SendBase
{
    [Range(-90, 90)] public double Latitude { get; set; }
    [Range(-180, 180)] public double Longitude { get; set; }
    public string? Title { get; set; }
    public string? Address { get; set; }
}

public sealed class SendContactRequest : SendBase
{
    [Required] public string ContactName { get; set; } = "";
    [Required] public string ContactPhone { get; set; } = "";
    public string? Organization { get; set; }
}

public sealed class SendReactionRequest
{
    [Required] public string Phone { get; set; } = "";
    [Required] public string MessageId { get; set; } = "";
    /// <summary>Emoji. String vazia remove a reação.</summary>
    public string Reaction { get; set; } = "";
}

public sealed class ReadMessageRequest
{
    [Required] public string Phone { get; set; } = "";
    public List<string>? MessageIds { get; set; }
}

public sealed class TypingRequest
{
    [Required] public string Phone { get; set; } = "";
    /// <summary>true = começa a digitar, false = para.</summary>
    public bool Typing { get; set; } = true;
}

public sealed record PhoneExistsDto(string Phone, bool Exists, string? ChatId);

public sealed record InstanceStatusDto(bool Connected, string Status, string? Phone, string? PushName, bool SmartphoneConnected);
