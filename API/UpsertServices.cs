// API/Functions/UpsertServices.cs
using API.Security;
using API.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedModels;
using System.Net;

namespace API.Functions;

public sealed class UpsertServices
{
    private readonly ILogger<UpsertServices> _log;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly IServiceRepository _repo;

    public UpsertServices(
        ILogger<UpsertServices> log,
        IConfiguration cfg,
        IHostEnvironment env,
        IServiceRepository repo)
    {
        _log = log;
        _cfg = cfg;
        _env = env;
        _repo = repo;
    }

    [Function("UpsertServices")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "services")] HttpRequestData req)
    {
        // Auth: SWA admin role OR x-api-key (if configured) OR Dev (no key)
        if (!StaticWebAppsAuth.IsAuthorizedAdmin(req, _cfg, _env))
        {
            var status = StaticWebAppsAuth.HasPrincipal(req)
                ? HttpStatusCode.Forbidden
                : HttpStatusCode.Unauthorized;

            var deny = req.CreateResponse(status);
            await deny.WriteStringAsync("Unauthorized.");
            return deny;
        }

        var ct = req.FunctionContext.CancellationToken;

        Service? input;
        try
        {
            input = await req.ReadFromJsonAsync<Service>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid JSON body for upsert.");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON.");
            return bad;
        }

        if (input is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Missing request body.");
            return bad;
        }

        if (string.IsNullOrWhiteSpace(input.Id))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Service.Id (slug) is required.");
            return bad;
        }

        // Normalize
        input.Sections ??= new List<ServiceSection>();

        try
        {
            // Save service + sections in one call
            await _repo.UpsertFullAsync(input, ct);

            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new { saved = input.Id }, ct);
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to upsert service {ServiceId}", input.Id);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Failed to save service.");
            return err;
        }
    }
}
