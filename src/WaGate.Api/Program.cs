using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using WaGate.Api.Auth;
using WaGate.Api.Endpoints;
using WaGate.Api.Infrastructure;
using WaGate.Api.Services;
using WaGate.Domain;
using WaGate.Infrastructure;
using WaGate.Infrastructure.Data;
using WaGate.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---------- Infra / dados ----------
builder.Services.AddInfrastructure(config);
builder.Services.AddOptions<SecurityOptions>().Bind(config.GetSection(SecurityOptions.Section));
builder.Services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section))
    .Validate(o => o.Key.Length >= 32, "Jwt:Key precisa ter pelo menos 32 caracteres.")
    .ValidateOnStart();

// ---------- Serviços de domínio ----------
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddSingleton<DeliverySignal>();
builder.Services.AddScoped<EventPublisher>();
builder.Services.AddScoped<WahaEventProcessor>();
builder.Services.AddScoped<InstanceService>();
builder.Services.AddScoped<MessageSender>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<InstanceTokenFilter>();

builder.Services.AddSingleton<WebhookDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
builder.Services.AddHostedService<StatusSyncService>();
builder.Services.AddHostedService<RetentionService>();

// ---------- HTTP ----------
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddValidation();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.SigningKey,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("instance", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.Request.RouteValues["instanceId"]?.ToString() ?? "anon",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = config.GetValue("RateLimit:InstanceBurst", 30),
            TokensPerPeriod = config.GetValue("RateLimit:InstancePerMinute", 60),
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? [])
     .AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddOpenApi("v1", o => o.AddDocumentTransformer<SecuritySchemesTransformer>());

var app = builder.Build();

// ---------- Banco ----------
await DatabaseSetup.InitializeAsync(app.Services, config, app.Logger);

// ---------- Pipeline ----------
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapScalarApiReference("/docs", o => o
    .WithTitle("WaGate API")
    .WithTheme(ScalarTheme.Default)
    .WithDefaultHttpClient(ScalarTarget.CSharp, ScalarClient.HttpClient)
    .AddPreferredSecuritySchemes(SecuritySchemesTransformer.Bearer)
    .WithCustomCss("""
        /* cores da marca WaGate */
        .light-mode { --scalar-color-accent: #00a578; --scalar-color-1: #01122f; --scalar-sidebar-color-active: #008462; }
        .dark-mode  { --scalar-color-accent: #01c38e; --scalar-background-1: #01122f; --scalar-background-2: #0d1b3a;
                      --scalar-background-3: #1c2a48; --scalar-sidebar-background-1: #01122f; }
        """));

app.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();
app.MapHealthChecks("/health");

app.MapAuth();
app.MapInstances();
app.MapWebhooks();
app.MapDashboard();
app.MapPublicApi();
app.MapWahaWebhook();
app.MapSystem();
app.MapBilling();

app.Run();

public partial class Program;

internal static class DatabaseSetup
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        if (!config.GetValue("Database:MigrateOnStartup", true)) return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaGateDbContext>();

        // SQL Server em container pode demorar alguns segundos para aceitar conexões
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning("Banco indisponível (tentativa {Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        // Conta inicial opcional (Seed:AdminEmail / Seed:AdminPassword)
        var email = config["Seed:AdminEmail"]?.Trim().ToLowerInvariant();
        var password = config["Seed:AdminPassword"];
        if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password) && !await db.Accounts.AnyAsync(a => a.Email == email))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Account>>();
            var account = new Account
            {
                Name = config["Seed:AdminName"] ?? "Administrador",
                Email = email,
                ClientToken = Crypto.NewToken(40),
                MaxInstances = config.GetValue("Seed:MaxInstances", 20),
                // conta interna: liberada sem passar pelo Stripe
                Plan = BillingPlan.Partner,
                SubscriptionStatus = "active",
                PlanQuantity = config.GetValue("Seed:MaxInstances", 20),
                TrialUsed = true
            };
            account.PasswordHash = hasher.HashPassword(account, password);
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            logger.LogInformation("Conta inicial {Email} criada", email);
        }
    }
}
