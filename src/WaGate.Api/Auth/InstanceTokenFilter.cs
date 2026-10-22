using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;

namespace WaGate.Api.Auth;

public sealed class SecurityOptions
{
    public const string Section = "Security";
    /// <summary>
    /// Força o header Client-Token em todas as contas, ignorando a opção "ativar/desativar" da tela Segurança.
    /// </summary>
    public bool EnforceClientToken { get; set; }
    public bool AllowRegistration { get; set; } = true;
}

/// <summary>
/// Autenticação estilo Z-API: /instances/{instanceId}/token/{token}/... + header Client-Token da conta.
/// A instância autenticada fica disponível em HttpContext.Items["instance"].
/// </summary>
public sealed class InstanceTokenFilter(WaGateDbContext db, IOptions<SecurityOptions> security, IOptions<WaGate.Infrastructure.Billing.StripeOptions> stripe) : IEndpointFilter
{
    public const string ItemKey = "instance";
    public const string ClientTokenHeader = "Client-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var route = http.Request.RouteValues;

        if (!Guid.TryParse(route["instanceId"]?.ToString(), out var instanceId))
            return Results.Problem(statusCode: 404, title: "Instância não encontrada");

        var token = route["token"]?.ToString();
        var instance = await db.Instances.Include(i => i.Account)
            .FirstOrDefaultAsync(i => i.Id == instanceId, http.RequestAborted);

        if (instance is null || !Crypto.FixedTimeEquals(instance.Token, token))
            return Results.Problem(statusCode: 401, title: "Instância ou token inválido");

        if (security.Value.EnforceClientToken || instance.Account!.ClientTokenEnabled)
        {
            var clientToken = http.Request.Headers[ClientTokenHeader].ToString();
            if (string.IsNullOrEmpty(clientToken))
                return Results.Problem(statusCode: 401, title: "Header Client-Token ausente");
            if (!Crypto.FixedTimeEquals(instance.Account!.ClientToken, clientToken))
                return Results.Problem(statusCode: 401, title: "Client-Token inválido");
        }

        if (stripe.Value.Enabled && !instance.Account!.HasUsableSubscription)
            return Results.Problem(statusCode: 402, title: "Assinatura inativa",
                detail: "A assinatura desta conta não está ativa. Regularize em Plano e cobrança no painel.");

        http.Items[ItemKey] = instance;
        return await next(ctx);
    }
}

public static class HttpContextInstanceExtensions
{
    public static Instance GetInstance(this HttpContext http) =>
        http.Items[InstanceTokenFilter.ItemKey] as Instance
        ?? throw new InvalidOperationException("Instância não resolvida.");
}
