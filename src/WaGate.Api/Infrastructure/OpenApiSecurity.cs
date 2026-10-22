using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WaGate.Api.Infrastructure;

/// <summary>Declara os esquemas de segurança (JWT do painel e Client-Token da API pública) no documento OpenAPI.</summary>
public sealed class SecuritySchemesTransformer : IOpenApiDocumentTransformer
{
    public const string Bearer = "Bearer";
    public const string ClientToken = "ClientToken";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info = new OpenApiInfo
        {
            Title = "WaGate API",
            Version = "v1",
            Description = """
                Gateway de WhatsApp sobre o **WAHA**, no estilo Z-API.

                **Dois tipos de autenticação**

                * **Painel / gestão** (`/api/*`): JWT obtido em `POST /api/auth/login`, enviado em `Authorization: Bearer <token>`.
                * **Envio** (`/instances/{instanceId}/token/{token}/*`): token da instância na URL + header `Client-Token` da conta.

                **Webhooks**

                Cada evento é entregue via `POST` em JSON com os headers `X-WaGate-Event`, `X-WaGate-Delivery`,
                `X-WaGate-Timestamp` e `X-WaGate-Signature: sha256=<hex>`, onde a assinatura é
                `HMAC-SHA256(secret, "{timestamp}.{body}")`. Falhas são repetidas com backoff exponencial (até 8 tentativas).
                """
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[Bearer] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT do painel (POST /api/auth/login)"
        };
        document.Components.SecuritySchemes[ClientToken] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "Client-Token",
            Description = "Token de segurança da conta (Painel → Conta)"
        };

        foreach (var (path, item) in document.Paths)
        {
            var scheme = path.StartsWith("/instances/") ? ClientToken
                : path.StartsWith("/api/") && !path.StartsWith("/api/auth/register") && !path.StartsWith("/api/auth/login") ? Bearer
                : null;
            if (scheme is null || item.Operations is null) continue;

            foreach (var op in item.Operations.Values)
            {
                op.Security ??= [];
                op.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(scheme, document)] = []
                });
            }
        }
        return Task.CompletedTask;
    }
}
