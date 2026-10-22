namespace WaGate.Domain;

/// <summary>Conta (tenant). Dona das instâncias, faz login no painel e possui o Client-Token de segurança.</summary>
public class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>Token de segurança da conta, enviado no header <c>Client-Token</c> nas chamadas públicas.</summary>
    public string ClientToken { get; set; } = "";
    /// <summary>Quando ativo, as rotas públicas exigem o header Client-Token.</summary>
    public bool ClientTokenEnabled { get; set; } = true;
    /// <summary>Limite usado quando a cobrança (Stripe) está desligada.</summary>
    public int MaxInstances { get; set; } = 5;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---- Cadastro ----
    /// <summary>Telefone normalizado (55 + DDD + número).</summary>
    public string? Phone { get; set; }
    /// <summary>CPF ou CNPJ, só dígitos. Único: impede repetir o período grátis com outra conta.</summary>
    public string? Document { get; set; }
    public string? CompanyName { get; set; }
    public DateTime? TermsAcceptedAt { get; set; }
    public string? SignupIp { get; set; }

    // ---- Segurança de login ----
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // ---- Assinatura ----
    public BillingPlan Plan { get; set; } = BillingPlan.None;
    /// <summary>Status do Stripe: trialing, active, past_due, canceled, unpaid, incomplete...</summary>
    public string SubscriptionStatus { get; set; } = "none";
    /// <summary>Quantidade de instâncias contratadas.</summary>
    public int PlanQuantity { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime? TrialEndsAt { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    /// <summary>O período grátis só pode ser usado uma vez por conta.</summary>
    public bool TrialUsed { get; set; }

    public List<Instance> Instances { get; set; } = [];

    /// <summary>Assinatura que libera uso (trial, ativa ou em atraso dentro da tolerância do Stripe).</summary>
    public bool HasUsableSubscription => SubscriptionStatus is "trialing" or "active" or "past_due";
}

public enum BillingPlan { None = 0, Ultimate = 1, Partner = 2 }

/// <summary>Eventos do Stripe já processados (idempotência do webhook).</summary>
public class StripeEventLog
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Instância = um número de WhatsApp conectado, mapeado 1:1 para uma sessão do WAHA.</summary>
public class Instance
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Account? Account { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Nome da sessão no WAHA (único).</summary>
    public string SessionName { get; set; } = "";
    /// <summary>Token da instância usado na URL pública (/instances/{id}/token/{token}/...).</summary>
    public string Token { get; set; } = "";
    public InstanceStatus Status { get; set; } = InstanceStatus.Created;
    public string? PhoneNumber { get; set; }
    public string? PushName { get; set; }
    public bool AutoReadMessages { get; set; }
    public bool RejectCalls { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StatusChangedAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public List<Webhook> Webhooks { get; set; } = [];
}

public enum InstanceStatus
{
    Created = 0,
    Stopped = 1,
    Starting = 2,
    ScanQrCode = 3,
    Working = 4,
    Failed = 5,
    Unknown = 99
}

/// <summary>Endpoint do cliente que recebe os eventos da instância.</summary>
public class Webhook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public Instance? Instance { get; set; }
    public string Url { get; set; } = "";
    /// <summary>Eventos assinados, separados por vírgula. "*" = todos.</summary>
    public string Events { get; set; } = "*";
    /// <summary>Segredo usado para assinar o corpo (HMAC-SHA256).</summary>
    public string Secret { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public IReadOnlyList<string> EventList =>
        Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool Accepts(string evt)
    {
        foreach (var e in EventList)
        {
            if (e == "*" || e.Equals(evt, StringComparison.OrdinalIgnoreCase)) return true;
            // curinga por prefixo: "message.*"
            if (e.EndsWith(".*") && evt.StartsWith(e[..^1], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InstanceId { get; set; }
    public Instance? Instance { get; set; }
    public string? ExternalId { get; set; }
    public string ChatId { get; set; } = "";
    public string? Phone { get; set; }
    public MessageDirection Direction { get; set; }
    public MessageType Type { get; set; }
    public string? Body { get; set; }
    public string? MediaUrl { get; set; }
    public string? FileName { get; set; }
    public string? SenderName { get; set; }
    public bool IsGroup { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public enum MessageDirection { Inbound = 0, Outbound = 1 }

public enum MessageType { Text, Image, Document, Audio, Video, Location, Contact, Reaction, Sticker, Other }

public enum MessageStatus
{
    Failed = -1,
    Pending = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Played = 4,
    Received = 10
}

/// <summary>Outbox de entregas de webhook com retentativa e backoff.</summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WebhookId { get; set; }
    public Webhook? Webhook { get; set; }
    public Guid InstanceId { get; set; }
    public string Event { get; set; } = "";
    public string Payload { get; set; } = "";
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public string? LastResponse { get; set; }
    public long? LastDurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
}

public enum DeliveryStatus { Pending = 0, Succeeded = 1, Failed = 2, Dead = 3 }

/// <summary>Log bruto dos eventos recebidos do WAHA (auditoria / depuração).</summary>
public class InboundEvent
{
    public long Id { get; set; }
    public string? WahaEventId { get; set; }
    public string Event { get; set; } = "";
    public string Session { get; set; } = "";
    public Guid? InstanceId { get; set; }
    public string Payload { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
