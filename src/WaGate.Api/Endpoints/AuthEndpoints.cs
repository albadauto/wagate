using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WaGate.Api.Auth;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;
using WaGate.Shared;

namespace WaGate.Api.Endpoints;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Autenticação");

        g.MapPost("/register", Register)
            .AllowAnonymous().RequireRateLimiting("auth")
            .WithSummary("Cria uma conta")
            .WithDescription("Cria a conta e devolve o JWT para o painel. Pode ser desativado com Security:AllowRegistration=false.");

        g.MapPost("/login", Login)
            .AllowAnonymous().RequireRateLimiting("auth")
            .WithSummary("Login com e-mail e senha");

        g.MapGet("/me", Me).RequireAuthorization().WithSummary("Dados da conta autenticada");

        g.MapPost("/client-token/rotate", RotateClientToken)
            .RequireAuthorization()
            .WithSummary("Gera um novo Client-Token")
            .WithDescription("Invalida imediatamente o token anterior em todas as integrações.");

        g.MapPut("/client-token/state", SetClientTokenState)
            .RequireAuthorization()
            .WithSummary("Ativa ou desativa a exigência do Client-Token")
            .WithDescription("Com o token desativado, as rotas de envio aceitam chamadas só com ID + token da instância.");

        return g;
    }

    private static async Task<IResult> SetClientTokenState(ClientTokenStateRequest req, HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([http.User.GetAccountId()], ct);
        if (account is null) return Results.Unauthorized();
        account.ClientTokenEnabled = req.Enabled;
        await db.SaveChangesAsync(ct);
        return Results.Ok(account.ToDto());
    }

    private const int MaxFailedLogins = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Senha123", "Senha@123", "Mudar123", "Admin123", "Admin@123", "Abc12345", "Qwerty123", "Password1", "Passw0rd",
        "12345678Aa", "Brasil123", "Whatsapp123", "Wagate123"
    };

    private static async Task<IResult> Register(
        RegisterRequest req, HttpContext http, WaGateDbContext db, JwtTokenService jwt, IPasswordHasher<Account> hasher,
        IOptions<SecurityOptions> security, ILoggerFactory logs, CancellationToken ct)
    {
        if (!security.Value.AllowRegistration)
            return Results.Problem(statusCode: 403, title: "Cadastro desativado");

        // Campo-armadilha preenchido = bot. Respondemos como sucesso genérico sem criar nada.
        if (!string.IsNullOrEmpty(req.Website))
        {
            logs.CreateLogger("Auth").LogWarning("Cadastro bloqueado pelo honeypot (IP {Ip})", http.Connection.RemoteIpAddress);
            return Results.Problem(statusCode: 400, title: "Não foi possível concluir o cadastro");
        }

        var errors = new Dictionary<string, string[]>();
        var email = req.Email.Trim().ToLowerInvariant();
        var document = BrDocs.Digits(req.Document);
        var name = string.Join(' ', req.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (!name.Contains(' ')) errors["Name"] = ["Informe nome e sobrenome."];
        if (CommonPasswords.Contains(req.Password) || req.Password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase))
            errors["Password"] = ["Senha muito fácil de adivinhar. Evite senhas comuns ou que contenham seu e-mail."];
        if (await db.Accounts.AnyAsync(a => a.Email == email, ct))
            errors["Email"] = ["Este e-mail já está cadastrado. Faça login ou use outro e-mail."];
        if (await db.Accounts.AnyAsync(a => a.Document == document, ct))
            errors["Document"] = ["Este CPF/CNPJ já possui uma conta."];
        if (errors.Count > 0) return Results.ValidationProblem(errors, title: "Revise os dados do cadastro");

        var account = new Account
        {
            Name = name,
            Email = email,
            Phone = BrDocs.NormalizePhone(req.Phone),
            Document = document,
            CompanyName = string.IsNullOrWhiteSpace(req.CompanyName) ? null : req.CompanyName.Trim(),
            TermsAcceptedAt = DateTime.UtcNow,
            SignupIp = http.Connection.RemoteIpAddress?.ToString(),
            ClientToken = Crypto.NewToken(40),
            LastLoginAt = DateTime.UtcNow
        };
        account.PasswordHash = hasher.HashPassword(account, req.Password);
        db.Accounts.Add(account);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // corrida entre dois cadastros com o mesmo e-mail/documento (índices únicos)
            return Results.Problem(statusCode: 409, title: "E-mail ou CPF/CNPJ já cadastrado");
        }

        var (token, exp) = jwt.Create(account);
        return Results.Ok(new AuthResponse(token, exp, account.ToDto()));
    }

    private static async Task<IResult> Login(
        LoginRequest req, WaGateDbContext db, JwtTokenService jwt, IPasswordHasher<Account> hasher, CancellationToken ct)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Email == email, ct);
        if (account is null)
        {
            hasher.HashPassword(new Account(), req.Password); // equaliza tempo de resposta
            return Results.Problem(statusCode: 401, title: "E-mail ou senha inválidos");
        }

        if (account.LockoutEnd > DateTime.UtcNow)
        {
            var minutes = (int)Math.Ceiling((account.LockoutEnd.Value - DateTime.UtcNow).TotalMinutes);
            return Results.Problem(statusCode: 423, title: "Conta bloqueada temporariamente",
                detail: $"Muitas tentativas de login. Tente novamente em {minutes} minuto(s).");
        }

        var result = hasher.VerifyHashedPassword(account, account.PasswordHash, req.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            account.FailedLoginCount++;
            if (account.FailedLoginCount >= MaxFailedLogins)
            {
                account.LockoutEnd = DateTime.UtcNow.Add(LockoutDuration);
                account.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            var left = MaxFailedLogins - account.FailedLoginCount;
            return account.LockoutEnd > DateTime.UtcNow
                ? Results.Problem(statusCode: 423, title: "Conta bloqueada temporariamente", detail: "Muitas tentativas de login. Tente novamente em 15 minutos.")
                : Results.Problem(statusCode: 401, title: "E-mail ou senha inválidos",
                    detail: left <= 2 ? $"Mais {left} tentativa(s) e a conta será bloqueada por 15 minutos." : null);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            account.PasswordHash = hasher.HashPassword(account, req.Password);
        account.FailedLoginCount = 0;
        account.LockoutEnd = null;
        account.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var (token, exp) = jwt.Create(account);
        return Results.Ok(new AuthResponse(token, exp, account.ToDto()));
    }

    private static async Task<IResult> Me(HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([http.User.GetAccountId()], ct);
        return account is null ? Results.Unauthorized() : Results.Ok(account.ToDto());
    }

    private static async Task<IResult> RotateClientToken(HttpContext http, WaGateDbContext db, CancellationToken ct)
    {
        var account = await db.Accounts.FindAsync([http.User.GetAccountId()], ct);
        if (account is null) return Results.Unauthorized();
        account.ClientToken = Crypto.NewToken(40);
        await db.SaveChangesAsync(ct);
        return Results.Ok(account.ToDto());
    }
}
