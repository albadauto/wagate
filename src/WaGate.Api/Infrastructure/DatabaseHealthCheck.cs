using Microsoft.Extensions.Diagnostics.HealthChecks;
using WaGate.Infrastructure.Data;

namespace WaGate.Api.Infrastructure;

public sealed class DatabaseHealthCheck(WaGateDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Sem conexão com o SQL Server");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Erro ao conectar no SQL Server", ex);
        }
    }
}
