using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WaGate.Api.Services;
using WaGate.Infrastructure.Waha;

namespace WaGate.Api.Infrastructure;

/// <summary>Converte exceções conhecidas em ProblemDetails (RFC 7807).</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title, detail) = ex switch
        {
            InstanceNotConnectedException e => (409, "Instância desconectada", e.Message),
            BillingException e => (e.Status, e.Status == 402 ? "Plano necessário" : "Não foi possível concluir", e.Message),
            InstanceLimitException e => (403, "Limite de instâncias", e.Message),
            WaGate.Infrastructure.Billing.StripeException e => (e.Status is 400 or 402 ? 400 : 502, "Erro no Stripe", e.Message),
            WahaApiException e when e.StatusCode is 404 => (404, "Recurso não encontrado no WAHA", e.Message),
            WahaApiException e when e.StatusCode is >= 400 and < 500 => (422, "Requisição recusada pelo WAHA", e.Message),
            WahaApiException e => (502, "Falha no WAHA", e.Message),
            HttpRequestException e => (503, "WAHA indisponível", e.Message),
            Polly.CircuitBreaker.BrokenCircuitException => (503, "WAHA indisponível", "Circuito aberto após falhas consecutivas. Tente novamente em instantes."),
            Polly.Timeout.TimeoutRejectedException => (504, "Tempo esgotado aguardando o WAHA", null),
            ArgumentException e => (400, "Requisição inválida", e.Message),
            UnauthorizedAccessException => (401, "Não autenticado", null),
            BadHttpRequestException e => (e.StatusCode, "Requisição inválida", e.Message),
            _ => (500, "Erro interno", null)
        };

        if (status >= 500) logger.LogError(ex, "Erro não tratado em {Path}", http.Request.Path);

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = ex,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = http.Request.Path }
        });
    }
}
