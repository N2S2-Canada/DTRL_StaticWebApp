using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using API.Services;
using SharedModels;

namespace API;

public sealed class UpsertServices
{
    private readonly IServiceRepository _repo;
    private readonly ILogger<UpsertServices> _log;
    private readonly string? _apiKey; // dev key optional

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public UpsertServices(IServiceRepository repo, ILogger<UpsertServices> log, IConfiguration cfg)
    {
        _repo = repo;
        _log = log;

        // Prefer a Services-specific key first; fall back to your existing Cms key if you want to reuse it
        _apiKey = cfg["ServicesAdminApiKey"]
               ?? cfg["CmsAdminApiKey"]
               ?? cfg["Values:ServicesAdminApiKey"]
               ?? cfg["Values:CmsAdminApiKey"];
    }

    [Function("UpsertServices")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "services")] HttpRequestData req,
        FunctionContext ctx)
    {
        // ---------- 1) Authorize ----------
        // In production SWA: locked down via staticwebapp.config.json to role "admin".
        // In local dev: allow X-API-KEY header equal to ServicesAdminApiKey/CmsAdminApiKey.
        if (!await IsAuthorizedAsync(req))
            return req.CreateResponse(HttpStatusCode.Unauthorized);

        // ---------- 2) Parse body (1 or many) ----------
        string body = await new StreamReader(req.Body).ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return await Fail(req, HttpStatusCode.BadRequest, "Empty body.");

        List<Service>? payload = null;

        // Try array first
        try { payload = JsonSerializer.Deserialize<List<Service>>(body, JsonOpts); } catch { /* ignore */ }

        // If not an array, try single object
        if (payload is null || payload.Count == 0)
        {
            try
            {
                var one = JsonSerializer.Deserialize<Service>(body, JsonOpts);
                if (one is not null) payload = new List<Service> { one };
            }
            catch { /* ignore */ }
        }

        if (payload is null || payload.Count == 0)
            return await Fail(req, HttpStatusCode.BadRequest, "Body must be a Service or an array of Services.");

        // ---------- 3) Normalize + save ----------
        foreach (var svc in payload)
        {
            // Ensure an Id/slug. If empty, derive from Title.
            if (string.IsNullOrWhiteSpace(svc.Id))
                svc.Id = Slugify(svc.Title);

            if (string.IsNullOrWhiteSpace(svc.Id))
                return await Fail(req, HttpStatusCode.BadRequest, "Each service needs an Id or a Title to slugify.");

            // Save card
            await _repo.UpsertServiceAsync(svc);

            // Replace sections atomically (batched by partition)
            await _repo.ReplaceSectionsAsync(svc.Id, svc.Sections ?? Enumerable.Empty<ServiceSection>());
        }

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    // ---------- helpers ----------

    private Task<bool> IsAuthorizedAsync(HttpRequestData req)
    {
        // 1) If running under SWA and route is restricted, SWA won’t even reach here unless you’re admin.
        // 2) Dev override: X-API-KEY in header equals configured _apiKey.
        if (!string.IsNullOrEmpty(_apiKey))
        {
            if (req.Headers.TryGetValues("x-api-key", out var vals) && vals.Any(v => v == _apiKey))
                return Task.FromResult(true);
        }

        // Optional: look at /.auth/me principal when running under SWA
        // (SWA Free/Local emulator may not pass this reliably to Functions)
        // Keep it simple: rely on route protection (admin) for prod.
        return Task.FromResult(false);
    }

    private static string Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var s = input.Trim().ToLowerInvariant();
        s = s.Replace('_', '-').Replace(' ', '-');
        s = Regex.Replace(s, @"[^a-z0-9\-]+", "-");
        s = Regex.Replace(s, @"-+", "-").Trim('-');
        return s;
    }

    private static async Task<HttpResponseData> Fail(HttpRequestData req, HttpStatusCode code, string message)
    {
        var res = req.CreateResponse(code);
        await res.WriteStringAsync(message);
        return res;
    }
}
