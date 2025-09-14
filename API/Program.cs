// Program.cs (Azure Functions .NET Isolated)
// - SQL auth if User Id/Password are present in connection string
// - Otherwise AAD token auth via Managed Identity -> ClientSecret -> DefaultAzureCredential
// - Registers AddDbContextFactory<AppDbContext> (inject IDbContextFactory<AppDbContext> in functions)

using System.Data.Common;
using System.Reflection;
using API.Data;                       // <-- your AppDbContext namespace
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureAppConfiguration(cfg =>
    {
        // Load in this order; later providers override earlier ones
        cfg.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
           .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true, reloadOnChange: true)
           .AddEnvironmentVariables();
    })
.ConfigureServices((ctx, services) =>
{
    var cfg = ctx.Configuration;
    var env = ctx.HostingEnvironment;

    // ---- Connection string ----
    var cs = cfg["SQLConnectionString"] ?? cfg.GetConnectionString("Sql")
             ?? throw new InvalidOperationException("Missing SQLConnectionString (or ConnectionStrings:Sql).");

    var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
    var usingSqlUserPassword = !string.IsNullOrWhiteSpace(csb.UserID) || !string.IsNullOrWhiteSpace(csb.Password);

    // ---- AAD credential (only if NOT using SQL user/password) ----
    if (!usingSqlUserPassword)
    {
        var tenantId = cfg["AzureAd:TenantId"] ?? cfg["TenantId"];
        var clientId = cfg["AzureAd:ClientId"] ?? cfg["ClientId"];
        var clientSec = cfg["AzureAd:ClientSecret"] ?? cfg["ClientSecret"];

        Azure.Core.TokenCredential credential;

        if (!string.IsNullOrWhiteSpace(tenantId) &&
            !string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(clientSec))
        {
            // Dev/local (fast): use the SP from secrets or local.settings.json
            credential = new Azure.Identity.ClientSecretCredential(tenantId, clientId, clientSec);
        }
        else if (!env.IsDevelopment())
        {
            // Prod without client secret: prefer Managed Identity
            credential = new Azure.Identity.ManagedIdentityCredential();
        }
        else
        {
            // Dev fallback: VS/AzCLI/etc., but skip MI to avoid slow IMDS probing locally
            credential = new Azure.Identity.DefaultAzureCredential(new Azure.Identity.DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = true
            });
        }

        services.AddSingleton<Azure.Core.TokenCredential>(credential);
        services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor, AadAccessTokenInterceptor>();
    }

    // ---- EF Core (pooled factory) ----
    services.AddPooledDbContextFactory<API.Data.AppDbContext>((sp, opts) =>
    {
        opts.UseSqlServer(cs, sql =>
        {
            sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
            sql.CommandTimeout(180);
        });

        if (!usingSqlUserPassword)
        {
            // Attach AAD token interceptor when using AAD auth
            opts.AddInterceptors(sp.GetRequiredService<Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor>());
        }
    });

    // ---- PageText server-side caching ----
    services.AddMemoryCache();
    services.AddSingleton<IPageTextCache, PageTextCache>();

    // Optional: warm EF model/connection on startup (speeds up first hit)
    services.AddHostedService<WarmupHostedService>();

    // (Optional) telemetry
    services.AddApplicationInsightsTelemetryWorkerService();
    services.ConfigureFunctionsApplicationInsights();
})

    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

await host.RunAsync();

/// <summary>
/// Interceptor that injects an Entra ID access token into each SqlConnection
/// when using AAD-based auth (scope: https://database.windows.net/.default).
/// </summary>
file sealed class AadAccessTokenInterceptor : DbConnectionInterceptor
{
    private static readonly TokenRequestContext Scope =
        new(["https://database.windows.net/.default"]);

    private readonly TokenCredential _credential;

    public AadAccessTokenInterceptor(TokenCredential credential) => _credential = credential;

    public override InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        if (connection is SqlConnection sql)
        {
            // Synchronous token for sync open paths
            sql.AccessToken = _credential.GetToken(Scope, default).Token;
        }
        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (connection is SqlConnection sql)
        {
            // Async token for async open paths
            sql.AccessToken = (await _credential.GetTokenAsync(Scope, cancellationToken)).Token;
        }
        return result;
    }
}
