using Microsoft.EntityFrameworkCore;
using WaGate.Api.Auth;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;
using WaGate.Infrastructure.Waha;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

/// <summary>Gestão de instâncias pelo painel (JWT).</summary>
public static class InstanceEndpoints
{
    public static RouteGroupBuilder MapInstances(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/instances").WithTags("Instâncias").RequireAuthorization();

        g.MapGet("/", List).WithSummary("Lista as instâncias da conta");
        g.MapPost("/", Create).WithSummary("Cria uma instância (e inicia a sessão no WAHA)");
        g.MapGet("/{id:guid}", Get).WithSummary("Detalhes da instância");
        g.MapPut("/{id:guid}", Update).WithSummary("Atualiza nome e preferências");
        g.MapDelete("/{id:guid}", Delete).WithSummary("Remove a instância e a sessão do WAHA");

        g.MapPost("/{id:guid}/start", (Guid id, HttpContext h, WaGateDbContext db, InstanceService s, CancellationToken ct) =>
            Act(id, h, db, ct, i => s.StartAsync(i, ct))).WithSummary("Inicia a sessão");
        g.MapPost("/{id:guid}/stop", (Guid id, HttpContext h, WaGateDbContext db, InstanceService s, CancellationToken ct) =>
            Act(id, h, db, ct, i => s.StopAsync(i, ct))).WithSummary("Para a sessão (mantém o login)");
        g.MapPost("/{id:guid}/restart", (Guid id, HttpContext h, WaGateDbContext db, InstanceService s, CancellationToken ct) =>
            Act(id, h, db, ct, i => s.RestartAsync(i, ct))).WithSummary("Reinicia a sessão");
        g.MapPost("/{id:guid}/logout", (Guid id, HttpContext h, WaGateDbContext db, InstanceService s, CancellationToken ct) =>
            Act(id, h, db, ct, i => s.LogoutAsync(i, ct))).WithSummary("Desconecta o número (exige novo QR)");
        g.MapPost("/{id:guid}/sync", (Guid id, HttpContext h, WaGateDbContext db, InstanceService s, CancellationToken ct) =>
            Act(id, h, db, ct, i => s.SyncAsync(i, ct))).WithSummary("Atualiza o status consultando o WAHA");

        g.MapGet("/{id:guid}/qr", Qr).WithSummary("QR Code para conectar (data URI base64)");
        g.MapPost("/{id:guid}/pairing-code", PairingCode).WithSummary("Código de pareamento por telefone (alternativa ao QR)");
        g.MapPost("/{id:guid}/token/rotate", RotateToken).WithSummary("Gera um novo token da instância");
        g.MapPost("/{id:guid}/send-text", SendTest).WithSummary("Envio de texto pelo painel");
        g.MapGet("/{id:guid}/messages", Messages).WithSummary("Histórico de mensagens (paginado)");

        return g;
    }

    internal static async Task<Instance?> Find(Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        return await db.Instances.FirstOrDefaultAsync(i => i.Id == id && i.AccountId == accountId, ct);
    }

    private static async Task<IResult> Act(Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct, Func<Instance, Task> action)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        await action(instance);
        var hooks = await db.Webhooks.CountAsync(w => w.InstanceId == id, ct);
        return Results.Ok(instance.ToDto(hooks));
    }

    private static async Task<IResult> List(HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var accountId = http.User.GetAccountId();
        var items = await db.Instances.AsNoTracking()
            .Where(i => i.AccountId == accountId)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new { i, hooks = i.Webhooks.Count })
            .ToListAsync(ct);
        return Results.Ok(items.Select(x => x.i.ToDto(x.hooks)).ToList());
    }

    private static async Task<IResult> Create(CreateInstanceRequest req, HttpContext http, InstanceService service, CancellationToken ct)
    {
        try
        {
            var instance = await service.CreateAsync(http.User.GetAccountId(), req, ct);
            return Results.Created($"/api/instances/{instance.Id}", instance.ToDto());
        }
        catch (InstanceLimitException ex)
        {
            return Results.Problem(statusCode: 403, title: "Limite de instâncias", detail: ex.Message);
        }
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        var hooks = await db.Webhooks.CountAsync(w => w.InstanceId == id, ct);
        return Results.Ok(instance.ToDto(hooks));
    }

    private static async Task<IResult> Update(Guid id, UpdateInstanceRequest req, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        instance.Name = req.Name.Trim();
        instance.AutoReadMessages = req.AutoReadMessages;
        instance.RejectCalls = req.RejectCalls;
        await db.SaveChangesAsync(ct);
        return Results.Ok(instance.ToDto());
    }

    private static async Task<IResult> Delete(Guid id, HttpContext http, WaGateDbContext db, InstanceService service, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        await service.DeleteAsync(instance, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Qr(Guid id, HttpContext http, WaGateDbContext db, InstanceService service, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        return Results.Ok(await service.GetQrAsync(instance, ct));
    }

    private static async Task<IResult> PairingCode(Guid id, PairingCodeRequest req, HttpContext http, WaGateDbContext db, IWahaClient waha, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        var code = await waha.RequestPairingCodeAsync(instance.SessionName, req.Phone, ct);
        return code is null
            ? Results.Problem(statusCode: 409, title: "O WAHA não retornou código. Use o QR Code.")
            : Results.Ok(new PairingCodeDto(code));
    }

    private static async Task<IResult> RotateToken(Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        instance.Token = Crypto.NewToken(32);
        await db.SaveChangesAsync(ct);
        return Results.Ok(instance.ToDto());
    }

    private static async Task<IResult> SendTest(Guid id, SendTextRequest req, HttpContext http, WaGateDbContext db, MessageSender sender, CancellationToken ct)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();
        return Results.Ok(await sender.TextAsync(instance, req, ct));
    }

    private static async Task<IResult> Messages(
        Guid id, HttpContext http, WaGateDbContext db, CancellationToken ct,
        int page = 1, int pageSize = 50, string? direction = null, string? search = null, string? phone = null)
    {
        var instance = await Find(id, http, db, ct);
        if (instance is null) return Results.NotFound();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var q = db.Messages.AsNoTracking().Where(m => m.InstanceId == id);
        if (Enum.TryParse<MessageDirection>(direction, true, out var dir)) q = q.Where(m => m.Direction == dir);
        if (!string.IsNullOrWhiteSpace(phone)) q = q.Where(m => m.Phone!.Contains(phone));
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(m => m.Body!.Contains(search));

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<MessageDto>(items.Select(m => m.ToDto()).ToList(), page, pageSize, total));
    }
}
