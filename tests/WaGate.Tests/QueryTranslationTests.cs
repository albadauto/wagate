using Microsoft.EntityFrameworkCore;
using WaGate.Domain;
using WaGate.Infrastructure.Data;

namespace WaGate.Tests;

/// <summary>
/// Garante que as consultas mais elaboradas traduzem para SQL Server
/// (ToQueryString não precisa de conexão com o banco).
/// </summary>
public class QueryTranslationTests
{
    private static WaGateDbContext Db() => new(new DbContextOptionsBuilder<WaGateDbContext>()
        .UseSqlServer("Server=nao-conecta;Database=x;TrustServerCertificate=True").Options);

    [Fact]
    public void Dashboard_daily_grouping_translates()
    {
        using var db = Db();
        var ids = new List<Guid> { Guid.NewGuid() };
        var since = DateTime.UtcNow.Date.AddDays(-6);

        var sql = db.Messages.Where(m => ids.Contains(m.InstanceId) && m.CreatedAt >= since)
            .GroupBy(m => new { m.CreatedAt.Date, m.Direction })
            .Select(g => new { g.Key.Date, g.Key.Direction, Count = g.Count() })
            .ToQueryString();

        Assert.Contains("GROUP BY", sql);
        Assert.Contains("CONVERT(date", sql);
    }

    [Fact]
    public void Deliveries_filter_by_account_through_navigation_translates()
    {
        using var db = Db();
        var accountId = Guid.NewGuid();
        var sql = db.WebhookDeliveries.Include(d => d.Webhook)
            .Where(d => d.Webhook!.Instance!.AccountId == accountId && d.Status == DeliveryStatus.Failed)
            .OrderByDescending(d => d.CreatedAt).Skip(25).Take(25)
            .ToQueryString();

        Assert.Contains("[Instances]", sql);
        Assert.Contains("OFFSET", sql);
    }

    [Fact]
    public void Dispatcher_candidate_query_translates()
    {
        using var db = Db();
        var now = DateTime.UtcNow;
        var sql = db.WebhookDeliveries
            .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed)
                        && d.NextAttemptAt <= now && (d.LockedUntil == null || d.LockedUntil < now))
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(25)
            .ToQueryString();

        Assert.Contains("'Pending'", sql); // enums gravados como texto
    }

    [Fact]
    public void Instance_list_with_webhook_count_translates()
    {
        using var db = Db();
        var accountId = Guid.NewGuid();
        var sql = db.Instances.Where(i => i.AccountId == accountId)
            .Select(i => new { i, hooks = i.Webhooks.Count })
            .ToQueryString();

        Assert.Contains("COUNT", sql);
    }

    [Fact]
    public void Model_has_expected_indexes()
    {
        using var db = Db();
        var msg = db.Model.FindEntityType(typeof(Message))!;
        Assert.Contains(msg.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual(["InstanceId", "ExternalId"]));
        var inst = db.Model.FindEntityType(typeof(Instance))!;
        Assert.Contains(inst.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == "SessionName");
    }
}
