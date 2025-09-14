using API.Data;
using API.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace API;

public sealed class UpsertPageText
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IPageTextCache _cache;
    private readonly ILogger<UpsertPageText> _logger;
    // fields:
    private readonly string? _apiKey;
    private readonly bool _devBypass;

    public UpsertPageText(
        IDbContextFactory<AppDbContext> dbFactory,
        IPageTextCache cache,
        ILogger<UpsertPageText> logger,
        IConfiguration cfg,
        IHostEnvironment env)
    {
        _dbFactory = dbFactory;
        _cache = cache;
        _logger = logger;

        // Read from either root or Values: (Functions puts settings under Values in local)
        _apiKey = cfg["CmsAdminApiKey"] ?? cfg["Values:CmsAdminApiKey"];

        // OPTIONAL: allow writes in local dev without a key (toggle with a flag)
        var allowDev = cfg["AllowDevCmsWrites"] ?? cfg["Values:AllowDevCmsWrites"];
        _devBypass = env.IsDevelopment() && string.Equals(allowDev, "true", StringComparison.OrdinalIgnoreCase);
    }
    // Accepts either a single object { key, content } or an array of those
    private sealed class Dto { public string Key { get; set; } = ""; public string Content { get; set; } = ""; }

    [Function("UpsertPageText")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pagetext")] HttpRequestData req)
    {
        // ---- Authorization: SWA admin role OR x-api-key match ----
        // ---- Authorization: SWA admin role OR x-api-key OR (optional) dev bypass ----
        var authorized = StaticWebAppsAuth.IsInRole(req, "admin");

        if (!authorized && !string.IsNullOrWhiteSpace(_apiKey)
            && req.Headers.TryGetValues("x-api-key", out var apis)
            && string.Equals(apis.FirstOrDefault(), _apiKey, StringComparison.Ordinal))
        {
            authorized = true;
        }

        if (!authorized && _devBypass)
        {
            _logger.LogWarning("DEV BYPASS enabled - accepting UpsertPageText without auth.");
            authorized = true;
        }

        if (!authorized)
        {
            var deny = req.CreateResponse(HttpStatusCode.Unauthorized);
            await deny.WriteStringAsync("Unauthorized.");
            return deny;
        }

        // ---- Read body ----
        Dto[] items;
        try
        {
            using var s = req.Body;
            items = await JsonSerializer.DeserializeAsync<Dto[]>(s, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? Array.Empty<Dto>();

            // If it wasn't an array, try single object
            if (items.Length == 0)
            {
                req.Body.Position = 0;
                var single = await JsonSerializer.DeserializeAsync<Dto>(req.Body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (single is not null) items = new[] { single };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid JSON for UpsertPageText.");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON.");
            return bad;
        }

        if (items.Length == 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("No items.");
            return bad;
        }

        // ---- Validate ----
        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.Key))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Key is required.");
                return bad;
            }
            if (i.Key.Length > 200)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync($"Key too long: {i.Key}");
                return bad;
            }
            // Optional content length limit
            if (i.Content?.Length > 20000)
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync($"Content too long for {i.Key}.");
                return bad;
            }
        }

        // ---- Upsert ----
        using var db = await _dbFactory.CreateDbContextAsync();
        foreach (var i in items)
        {
            var existing = await db.PageTexts.FirstOrDefaultAsync(p => p.Key == i.Key);
            if (existing is null)
            {
                db.PageTexts.Add(new PageText { Key = i.Key, Content = i.Content ?? "" });
            }
            else
            {
                existing.Content = i.Content ?? "";
                db.PageTexts.Update(existing);
            }
        }

        await db.SaveChangesAsync();

        // Invalidate server cache so new values show up immediately
        _cache.Invalidate();

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new { updated = items.Select(i => i.Key).ToArray() });
        return ok;
    }
}
