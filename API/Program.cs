// Program.cs — Functions (isolated), Tables-backed CMS, User Secrets in Development

using System.Reflection;
using API.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    // Load extra configuration sources BEFORE the Functions host starts.
    .ConfigureAppConfiguration((context, config) =>
    {
        // The Functions host already maps local.settings.json -> environment variables under "Values:*".
        // Here we add User Secrets in Development so keys like "CmsAdminApiKey" are available locally
        // without putting them in local.settings.json.
        if (context.HostingEnvironment.IsDevelopment())
        {
            config.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        }

        // Environment variables are included by default; nothing else needed here.
    })
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((ctx, services) =>
    {
        // Observability (keep if you had it)
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // In-memory cache for CMS text
        services.AddMemoryCache();
        services.AddSingleton<IPageTextCache, PageTextCache>(); // your cache implementation updated for Tables

        // Azure Table Storage repository for PageText
        services.AddSingleton<IPageTextRepository, TablePageTextRepository>();

        // Optional but recommended: warm up cache from Tables at host start
        services.AddHostedService<WarmupHostedService>();
        services.AddSingleton<IServiceRepository, TableServiceRepository>();

        // NOTE: No EF/DbContext/Sql interceptors here anymore.
        // Your repository will use either:
        //   - StorageConnectionString (access key)  OR
        //   - StorageAccountUrl + DefaultAzureCredential (if you later enable MI/SP)
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

host.Run();


// ------------------------------
// WarmupHostedService
// (Keep this ONLY if you don't already have one elsewhere.)
// ------------------------------
public sealed class WarmupHostedService : IHostedService
{
    private readonly IPageTextRepository _repo;
    private readonly IPageTextCache _cache;
    private readonly ILogger<WarmupHostedService> _log;

    public WarmupHostedService(IPageTextRepository repo, IPageTextCache cache, ILogger<WarmupHostedService> log)
    {
        _repo = repo;
        _cache = cache;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var data = await _repo.GetAllAsync(ct);
            _cache.Seed(new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase));
            _log.LogInformation("PageText cache primed from Azure Table Storage.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Warmup failed; will load on first request.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
