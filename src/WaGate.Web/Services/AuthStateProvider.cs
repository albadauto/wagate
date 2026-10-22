using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace WaGate.Web.Services;

/// <summary>
/// Guarda o JWT da API no navegador (ProtectedLocalStorage, cifrado pelo Data Protection)
/// e expõe o usuário ao Blazor.
/// </summary>
public sealed class AuthStateProvider(ProtectedLocalStorage storage, ILogger<AuthStateProvider> logger) : AuthenticationStateProvider
{
    private const string Key = "wagate.session";
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private StoredSession? _session;
    private bool _loaded;

    public string? Token => _session is { } s && s.ExpiresAt > DateTime.UtcNow ? s.Token : null;
    public string? Name => _session?.Name;
    public string? Email => _session?.Email;

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try
            {
                var r = await storage.GetAsync<StoredSession>(Key);
                _session = r.Success ? r.Value : null;
            }
            catch (Exception ex)
            {
                // chave do Data Protection trocada ou storage indisponível
                logger.LogDebug(ex, "Não foi possível ler a sessão salva");
                _session = null;
            }
        }
        return Build();
    }

    public async Task SignInAsync(string token, DateTime expiresAt, string name, string email)
    {
        _session = new StoredSession(token, expiresAt, name, email);
        _loaded = true;
        await storage.SetAsync(Key, _session);
        NotifyAuthenticationStateChanged(Task.FromResult(Build()));
    }

    public async Task SignOutAsync()
    {
        _session = null;
        _loaded = true;
        try { await storage.DeleteAsync(Key); } catch { /* circuito encerrando */ }
        NotifyAuthenticationStateChanged(Task.FromResult(Anonymous));
    }

    private AuthenticationState Build()
    {
        if (_session is null || _session.ExpiresAt <= DateTime.UtcNow) return Anonymous;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, _session.Name),
            new Claim(ClaimTypes.Email, _session.Email)
        ], "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public sealed record StoredSession(string Token, DateTime ExpiresAt, string Name, string Email);
}
