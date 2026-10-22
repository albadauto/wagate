using WaGate.Domain;

namespace WaGate.Infrastructure.Billing;

public sealed class StripeOptions
{
    public const string Section = "Stripe";

    /// <summary>sk_test_... ou sk_live_.... Vazio = cobrança desligada (uso livre, limitado por Account.MaxInstances).</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>whsec_... do endpoint de webhook (Dashboard → Developers → Webhooks, ou `stripe listen`).</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Versão da API fixada para respostas estáveis.</summary>
    public string ApiVersion { get; set; } = "2024-06-20";

    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey);
}

public sealed class BillingOptions
{
    public const string Section = "Billing";

    public string Currency { get; set; } = "brl";

    /// <summary>URL do painel (para os retornos do Checkout e do Portal).</summary>
    public string PublicAppUrl { get; set; } = "http://localhost:5090";

    public PlanOptions Ultimate { get; set; } = new()
    {
        Name = "Ultimate",
        UnitAmount = 9900,
        TrialDays = 7,
        MinQuantity = 1,
        MaxQuantity = 9,
        Description = "Para empresas que querem automatizar o WhatsApp com tudo liberado.",
        Features =
        [
            "Mensagens ilimitadas",
            "Texto, imagem, áudio, vídeo, documento e localização",
            "Webhooks assinados com retentativa automática",
            "Logs de entregas e histórico de mensagens",
            "Token de segurança da conta",
            "7 dias grátis para testar"
        ]
    };

    public PlanOptions Partner { get; set; } = new()
    {
        Name = "Partner",
        UnitAmount = 8990,
        TrialDays = 0,
        MinQuantity = 10,
        MaxQuantity = 2000,
        Description = "Para software houses, integradores e plataformas com 10 instâncias ou mais.",
        Features =
        [
            "Tudo do Plano Ultimate",
            "API de gestão de instâncias",
            "Preço reduzido por instância",
            "Ajuste a quantidade quando quiser (cobrança proporcional)"
        ]
    };

    public PlanOptions Get(BillingPlan plan) => plan switch
    {
        BillingPlan.Ultimate => Ultimate,
        BillingPlan.Partner => Partner,
        _ => throw new ArgumentOutOfRangeException(nameof(plan), "Plano inválido.")
    };
}

public sealed class PlanOptions
{
    public string Name { get; set; } = "";
    /// <summary>Valor em centavos por instância/mês.</summary>
    public long UnitAmount { get; set; }
    public int TrialDays { get; set; }
    public int MinQuantity { get; set; } = 1;
    public int MaxQuantity { get; set; } = 1;
    public string Description { get; set; } = "";
    public List<string> Features { get; set; } = [];
    /// <summary>Opcional: price_... já existente no Stripe. Se vazio, o preço é criado automaticamente via lookup_key.</summary>
    public string? PriceId { get; set; }

    public string LookupKey(BillingPlan plan, string currency) =>
        $"wagate_{plan.ToString().ToLowerInvariant()}_{UnitAmount}_{currency}_month";
}
