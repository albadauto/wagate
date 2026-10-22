using Microsoft.Extensions.Http.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Waha;

namespace WaGate.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<WaGateDbContext>(o =>
            o.UseSqlServer(config.GetConnectionString("Default"), sql =>
            {
                sql.EnableRetryOnFailure(5);
                sql.MigrationsAssembly(typeof(WaGateDbContext).Assembly.FullName);
            }));

        services.AddOptions<WahaOptions>().Bind(config.GetSection(WahaOptions.Section));

        services.AddHttpClient<IWahaClient, WahaClient>((sp, http) =>
            {
                var opt = sp.GetRequiredService<IOptions<WahaOptions>>().Value;
                http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = Timeout.InfiniteTimeSpan; // os timeouts ficam no pipeline de resiliência
                if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                    http.DefaultRequestHeaders.Add("X-Api-Key", opt.ApiKey);
            })
            .AddStandardResilienceHandler(o =>
            {
                // Envios (POST/PUT) não são idempotentes: repetir após um timeout pode mandar a mesma
                // mensagem duas vezes. Só GET/HEAD são repetidos.
                o.Retry.MaxRetryAttempts = 2;
                o.Retry.DisableForUnsafeHttpMethods();
                // conversão de áudio/vídeo no WAHA pode levar dezenas de segundos
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(150);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
            });

        // Stripe (cobrança). Sem Stripe:SecretKey a cobrança fica desligada.
        services.AddOptions<Billing.StripeOptions>().Bind(config.GetSection(Billing.StripeOptions.Section));
        services.AddOptions<Billing.BillingOptions>().Bind(config.GetSection(Billing.BillingOptions.Section));
        services.AddHttpClient<Billing.StripeClient>(http =>
        {
            http.BaseAddress = new Uri("https://api.stripe.com/");
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        // Cliente usado para entregar webhooks aos clientes (sem retry: o outbox cuida disso).
        services.AddHttpClient("webhooks", http =>
        {
            http.Timeout = TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WaGate-Webhooks/1.0");
        });

        return services;
    }

    public static InstanceStatus MapWahaStatus(string? status) => status?.ToUpperInvariant() switch
    {
        "STOPPED" => InstanceStatus.Stopped,
        "STARTING" => InstanceStatus.Starting,
        "SCAN_QR_CODE" => InstanceStatus.ScanQrCode,
        "PASSKEY_REQUIRED" or "PASSKEY_CONFIRMATION_REQUIRED" => InstanceStatus.ScanQrCode,
        "WORKING" => InstanceStatus.Working,
        "FAILED" => InstanceStatus.Failed,
        null or "" => InstanceStatus.Unknown,
        _ => InstanceStatus.Unknown
    };
}
