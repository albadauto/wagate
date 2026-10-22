using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using WaGate.Web.Components;
using WaGate.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Chaves do Data Protection persistidas para a sessão salva sobreviver a restarts/containers
var keysPath = config["DataProtection:KeysPath"];
var dp = builder.Services.AddDataProtection().SetApplicationName("WaGate.Web");
if (!string.IsNullOrWhiteSpace(keysPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<AuthStateProvider>());
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<SubscriptionState>();

builder.Services.AddHttpClient<ApiClient>(http =>
{
    http.BaseAddress = new Uri((config["Api:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(100);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();
app.MapStaticAssets();
// A sessão vive no navegador (ProtectedLocalStorage); quem protege as páginas é o
// AuthorizeRouteView no circuito interativo, então o endpoint HTTP é anônimo.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AllowAnonymous();

app.Run();
