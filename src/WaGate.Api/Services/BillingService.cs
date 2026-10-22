using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaGate.Domain;
using WaGate.Infrastructure.Billing;
using WaGate.Infrastructure.Data;
using WaGate.Shared;

namespace WaGate.Api.Services;

public sealed class BillingException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Regras de plano/assinatura e sincronização com o Stripe.</summary>
public sealed class BillingService(
    WaGateDbContext db,
    StripeClient stripe,
    IOptions<StripeOptions> stripeOptions,
    IOptions<BillingOptions> billingOptions,
    ILogger<BillingService> logger)
{
    private readonly BillingOptions _plans = billingOptions.Value;
    public bool Enabled => stripeOptions.Value.Enabled;

    public IReadOnlyList<PlanDto> Plans() =>
    [
        ToDto(BillingPlan.Ultimate, _plans.Ultimate),
        ToDto(BillingPlan.Partner, _plans.Partner)
    ];

    private PlanDto ToDto(BillingPlan key, PlanOptions p) =>
        new(key.ToString(), p.Name, p.Description, p.UnitAmount, _plans.Currency, p.TrialDays, p.MinQuantity, p.MaxQuantity, p.Features);

    /// <summary>Quantas instâncias a conta pode ter agora.</summary>
    public int InstanceLimit(Account a) =>
        !Enabled ? a.MaxInstances : a.HasUsableSubscription ? a.PlanQuantity : 0;

    public bool TrialAvailable(Account a) => !a.TrialUsed && _plans.Ultimate.TrialDays > 0 && !a.HasUsableSubscription;

    public async Task<SubscriptionDto> GetAsync(Account a, CancellationToken ct)
    {
        var used = await db.Instances.CountAsync(i => i.AccountId == a.Id, ct);
        return new SubscriptionDto(
            Enabled, a.Plan.ToString(), a.SubscriptionStatus, a.PlanQuantity, used, InstanceLimit(a),
            TrialAvailable(a), a.TrialEndsAt, a.CurrentPeriodEnd, a.CancelAtPeriodEnd, a.StripeCustomerId is not null);
    }

    public async Task<string> CheckoutAsync(Account a, string planName, int quantity, CancellationToken ct)
    {
        EnsureEnabled();
        var plan = ParsePlan(planName);
        var p = _plans.Get(plan);
        if (quantity < p.MinQuantity || quantity > p.MaxQuantity)
            throw new BillingException(400, $"O plano {p.Name} aceita de {p.MinQuantity} a {p.MaxQuantity} instâncias.");
        if (a.HasUsableSubscription)
            throw new BillingException(409, "Você já tem uma assinatura ativa. Use “Alterar plano” para trocar de plano ou quantidade.");

        a.StripeCustomerId ??= await stripe.CreateCustomerAsync(a, ct);
        await db.SaveChangesAsync(ct);

        var withTrial = plan == BillingPlan.Ultimate && TrialAvailable(a);
        return await stripe.CreateCheckoutAsync(a, plan, quantity, withTrial, ct);
    }

    public async Task<string> PortalAsync(Account a, CancellationToken ct)
    {
        EnsureEnabled();
        if (a.StripeCustomerId is null) throw new BillingException(409, "Assine um plano primeiro.");
        return await stripe.CreatePortalAsync(a.StripeCustomerId, ct);
    }

    /// <summary>Troca de plano e/ou quantidade numa assinatura existente.</summary>
    public async Task ChangeAsync(Account a, string planName, int quantity, CancellationToken ct)
    {
        EnsureEnabled();
        var plan = ParsePlan(planName);
        var p = _plans.Get(plan);
        if (quantity < p.MinQuantity || quantity > p.MaxQuantity)
            throw new BillingException(400, $"O plano {p.Name} aceita de {p.MinQuantity} a {p.MaxQuantity} instâncias.");
        if (a.StripeSubscriptionId is null || !a.HasUsableSubscription)
            throw new BillingException(409, "Nenhuma assinatura ativa para alterar.");

        var used = await db.Instances.CountAsync(i => i.AccountId == a.Id, ct);
        if (quantity < used)
            throw new BillingException(409, $"Você tem {used} instâncias. Exclua algumas antes de reduzir para {quantity}.");

        var current = await stripe.GetSubscriptionAsync(a.StripeSubscriptionId, ct);
        // o período grátis vale só para o Ultimate: ao migrar para o Partner, a cobrança começa agora
        var endTrial = current.Status == "trialing" && plan != BillingPlan.Ultimate;
        var updated = await stripe.UpdateSubscriptionAsync(current, plan, quantity, endTrial, ct);
        Apply(a, updated);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Atualiza a conta a partir do Stripe (usado no retorno do Checkout e nos webhooks).</summary>
    public async Task SyncAsync(Account a, string? checkoutSessionId, CancellationToken ct)
    {
        EnsureEnabled();
        var subId = a.StripeSubscriptionId;
        if (checkoutSessionId is not null)
            subId = await stripe.GetCheckoutSubscriptionIdAsync(checkoutSessionId, ct) ?? subId;
        if (subId is null) return;

        Apply(a, await stripe.GetSubscriptionAsync(subId, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task SyncSubscriptionAsync(string subscriptionId, CancellationToken ct)
    {
        var sub = await stripe.GetSubscriptionAsync(subscriptionId, ct);
        var account = await FindAccount(sub, ct);
        if (account is null)
        {
            logger.LogWarning("Assinatura {Sub} do Stripe sem conta correspondente", subscriptionId);
            return;
        }
        // eventos antigos de uma assinatura substituída não podem sobrescrever a atual
        if (account.StripeSubscriptionId is not null && account.StripeSubscriptionId != sub.Id &&
            account.HasUsableSubscription && sub.Status is "canceled" or "incomplete_expired")
            return;

        Apply(account, sub);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Conta {Account}: plano {Plan} ({Status}, {Qty} instância(s))", account.Id, account.Plan, account.SubscriptionStatus, account.PlanQuantity);
    }

    private async Task<Account?> FindAccount(StripeSubscription sub, CancellationToken ct)
    {
        if (Guid.TryParse(sub.MetadataAccountId, out var id))
        {
            var byId = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (byId is not null) return byId;
        }
        return await db.Accounts.FirstOrDefaultAsync(a => a.StripeCustomerId == sub.CustomerId, ct);
    }

    public static void Apply(Account a, StripeSubscription s)
    {
        a.StripeSubscriptionId = s.Id;
        a.StripeCustomerId ??= s.CustomerId;
        a.SubscriptionStatus = s.Status;
        a.PlanQuantity = s.Quantity;
        if (s.Plan != BillingPlan.None) a.Plan = s.Plan;
        a.TrialEndsAt = s.Status == "trialing" ? s.TrialEnd : a.TrialEndsAt;
        a.CurrentPeriodEnd = s.CurrentPeriodEnd;
        a.CancelAtPeriodEnd = s.CancelAtPeriodEnd;
        if (s.TrialEnd is not null) a.TrialUsed = true;
    }

    private static BillingPlan ParsePlan(string name) =>
        Enum.TryParse<BillingPlan>(name, true, out var p) && p != BillingPlan.None
            ? p
            : throw new BillingException(400, "Plano inválido. Use Ultimate ou Partner.");

    private void EnsureEnabled()
    {
        if (!Enabled) throw new BillingException(503, "A cobrança não está configurada neste servidor (Stripe:SecretKey).");
    }
}
