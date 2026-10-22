using System.ComponentModel.DataAnnotations;

namespace WaGate.Shared;

// ---------- Autenticação ----------

public sealed class RegisterRequest
{
    [Required(ErrorMessage = "Informe seu nome."), StringLength(120, MinimumLength = 3, ErrorMessage = "Nome deve ter entre 3 e 120 caracteres.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Informe o e-mail."), EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(200)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Informe o telefone."), BrPhone]
    public string Phone { get; set; } = "";

    [Required(ErrorMessage = "Informe o CPF ou CNPJ."), CpfCnpj]
    public string Document { get; set; } = "";

    [StringLength(160)]
    public string? CompanyName { get; set; }

    [Required(ErrorMessage = "Crie uma senha."), StringLength(100), StrongPassword]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Confirme a senha."), Compare(nameof(Password), ErrorMessage = "As senhas não conferem.")]
    public string ConfirmPassword { get; set; } = "";

    [MustBeTrue(ErrorMessage = "É preciso aceitar os termos de uso e a política de privacidade.")]
    public bool AcceptTerms { get; set; }

    /// <summary>Campo-armadilha anti-bot: deve vir vazio (fica oculto no formulário).</summary>
    public string? Website { get; set; }
}

public sealed class LoginRequest
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required] public string Password { get; set; } = "";
}

public sealed record AuthResponse(string AccessToken, DateTime ExpiresAt, AccountDto Account);

public sealed record AccountDto(Guid Id, string Name, string Email, string ClientToken, bool ClientTokenEnabled, int MaxInstances, DateTime CreatedAt,
    string? Phone = null, string? Document = null, string? CompanyName = null);

public sealed class ClientTokenStateRequest
{
    public bool Enabled { get; set; }
}

// ---------- Instâncias ----------

public sealed class CreateInstanceRequest
{
    [Required, StringLength(80, MinimumLength = 2)] public string Name { get; set; } = "";
    public bool AutoReadMessages { get; set; }
    public bool RejectCalls { get; set; }
    /// <summary>Se verdadeiro, já inicia a sessão no WAHA para gerar o QR Code.</summary>
    public bool Start { get; set; } = true;
}

public sealed class UpdateInstanceRequest
{
    [Required, StringLength(80, MinimumLength = 2)] public string Name { get; set; } = "";
    public bool AutoReadMessages { get; set; }
    public bool RejectCalls { get; set; }
}

public sealed record InstanceDto(
    Guid Id,
    string Name,
    string SessionName,
    string Token,
    string Status,
    string? PhoneNumber,
    string? PushName,
    bool AutoReadMessages,
    bool RejectCalls,
    DateTime CreatedAt,
    DateTime? StatusChangedAt,
    DateTime? ConnectedAt,
    int WebhookCount);

public sealed record QrCodeDto(string Status, string? Image, string? Raw, string? Message);

public sealed class PairingCodeRequest
{
    [Required, RegularExpression(@"^\+?\d{10,15}$")] public string Phone { get; set; } = "";
}

public sealed record PairingCodeDto(string Code);

// ---------- Webhooks ----------

public sealed class WebhookRequest
{
    [Required, Url, StringLength(500)] public string Url { get; set; } = "";
    /// <summary>Lista de eventos. Vazio ou ["*"] = todos. Aceita curinga "message.*".</summary>
    public List<string> Events { get; set; } = ["*"];
    public bool IsActive { get; set; } = true;
    [StringLength(200)] public string? Description { get; set; }
}

public sealed record WebhookDto(Guid Id, Guid InstanceId, string Url, IReadOnlyList<string> Events, string Secret, bool IsActive, string? Description, DateTime CreatedAt);

public sealed record DeliveryDto(
    Guid Id,
    Guid WebhookId,
    string WebhookUrl,
    Guid InstanceId,
    string Event,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTime NextAttemptAt,
    int? LastStatusCode,
    string? LastError,
    long? LastDurationMs,
    DateTime CreatedAt,
    DateTime? DeliveredAt);

public sealed record DeliveryDetailDto(DeliveryDto Delivery, string Payload, string? LastResponse);

// ---------- Mensagens ----------

public sealed record MessageDto(
    Guid Id,
    Guid InstanceId,
    string? ExternalId,
    string ChatId,
    string? Phone,
    string Direction,
    string Type,
    string? Body,
    string? MediaUrl,
    string? FileName,
    string? SenderName,
    bool IsGroup,
    string Status,
    string? Error,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record DashboardDto(
    int Instances,
    int InstancesConnected,
    int MessagesToday,
    int MessagesInboundToday,
    int MessagesOutboundToday,
    int DeliveriesPending,
    int DeliveriesFailed24h,
    double DeliverySuccessRate24h,
    IReadOnlyList<DailyCount> Last7Days);

public sealed record DailyCount(DateOnly Day, int Inbound, int Outbound);

public sealed record SendResultDto(string ZaapId, string? MessageId, string Id);

public sealed record ApiError(string Error, string? Detail = null);

// ---------- Planos e assinatura ----------

public sealed record PlanDto(
    string Key, string Name, string Description, long UnitAmount, string Currency, int TrialDays,
    int MinQuantity, int MaxQuantity, IReadOnlyList<string> Features);

public sealed record SubscriptionDto(
    bool BillingEnabled,
    string Plan,
    string Status,
    int Quantity,
    int InstancesUsed,
    int InstanceLimit,
    bool TrialAvailable,
    DateTime? TrialEndsAt,
    DateTime? CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    bool HasStripeCustomer)
{
    public bool IsUsable => Status is "trialing" or "active" or "past_due";
}

public sealed class CheckoutRequest
{
    /// <summary>"Ultimate" ou "Partner".</summary>
    [System.ComponentModel.DataAnnotations.Required] public string Plan { get; set; } = "Ultimate";
    [System.ComponentModel.DataAnnotations.Range(1, 10000)] public int Quantity { get; set; } = 1;
}

public sealed record RedirectDto(string Url);
