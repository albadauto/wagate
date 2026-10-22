namespace WaGate.Api.Services;

/// <summary>Backoff exponencial das entregas de webhook: 10s, 30s, 2min, 10min, 30min, 1h, 3h, 6h (±10% jitter).</summary>
public static class RetryPolicy
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6)
    ];

    /// <param name="attempt">Número de tentativas já realizadas (1 = a primeira falhou).</param>
    /// <param name="jitter">Aplica variação aleatória de ±10%.</param>
    public static TimeSpan Delay(int attempt, bool jitter = true)
    {
        var idx = Math.Clamp(attempt - 1, 0, Schedule.Length - 1);
        var baseDelay = Schedule[idx];
        if (!jitter) return baseDelay;
        var factor = 0.9 + Random.Shared.NextDouble() * 0.2;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }

    /// <summary>410 Gone: o receptor declarou que o endpoint não existe mais — não repetir.</summary>
    public static bool IsPermanentFailure(int statusCode) => statusCode == 410;
}
