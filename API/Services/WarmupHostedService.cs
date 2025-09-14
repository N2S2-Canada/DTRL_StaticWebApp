using System.Threading;
using System.Threading.Tasks;
using API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class WarmupHostedService : IHostedService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IPageTextCache _cache;
    private readonly ILogger<WarmupHostedService> _logger;

    public WarmupHostedService(
        IDbContextFactory<AppDbContext> factory,
        IPageTextCache cache,
        ILogger<WarmupHostedService> logger)
    {
        _factory = factory;
        _cache = cache;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var db = await _factory.CreateDbContextAsync(ct);

            // Touch the DB/EF model and open a connection (triggers token acquisition once).
            await db.Database.CanConnectAsync(ct);
            _ = await db.PageTexts.AsNoTracking().Take(1).ToListAsync(ct);

            // Prime the in-memory PageText cache (so first request is instant).
            _ = await _cache.GetAllAsync(ct);

            _logger.LogInformation("Warmup complete: EF model, SQL connection, and PageText cache primed.");
        }
        catch (Exception ex)
        {
            // Warmup is best-effort; don’t block the host if it fails.
            _logger.LogWarning(ex, "Warmup failed; continuing without preloading.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
