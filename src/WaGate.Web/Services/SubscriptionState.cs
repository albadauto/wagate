using WaGate.Shared;

namespace WaGate.Web.Services;

/// <summary>Assinatura da conta em cache por circuito, para banners e bloqueios no painel.</summary>
public sealed class SubscriptionState(ApiClient api)
{
    public SubscriptionDto? Current { get; private set; }
    public event Action? Changed;

    public async Task<SubscriptionDto?> LoadAsync(bool force = false)
    {
        if (Current is not null && !force) return Current;
        try { Current = await api.SubscriptionAsync(); }
        catch (ApiException) { /* sem sessão ou API fora: não bloqueia a tela */ }
        Changed?.Invoke();
        return Current;
    }

    public void Set(SubscriptionDto dto)
    {
        Current = dto;
        Changed?.Invoke();
    }

    public void Clear() => Current = null;
}

public static class Money
{
    private static readonly System.Globalization.CultureInfo Br = new("pt-BR");
    public static string Brl(long cents) => (cents / 100m).ToString("C", Br);
    public static string Date(DateTime? d) => d?.ToLocalTime().ToString("dd/MM/yyyy", Br) ?? "—";
}
