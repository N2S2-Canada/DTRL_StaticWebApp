using System.Net;
using API.Security;
using API.Services; // ICustomerContentRepository with PurgeExpiredAsync()
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace API.Functions;

public sealed class PurgeExpiredCustomerContentHttp
{
    private readonly ILogger<PurgeExpiredCustomerContentHttp> _log;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ICustomerContentRepository _repo;

    public PurgeExpiredCustomerContentHttp(
        ILogger<PurgeExpiredCustomerContentHttp> log,
        IConfiguration cfg,
        IHostEnvironment env,
        ICustomerContentRepository repo)
    {
        _log = log;
        _cfg = cfg;
        _env = env;
        _repo = repo;
    }

    // POST /api/customer-content/purge
    [Function("PurgeExpiredCustomerContent")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customer-content/purge")] HttpRequestData req)
    {
        // Require SWA admin OR x-api-key (ServicesAdminApiKey/CmsAdminApiKey).
        if (!StaticWebAppsAuth.IsAuthorizedAdmin(req, _cfg, _env))
        {
            var status = StaticWebAppsAuth.HasPrincipal(req) ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            var deny = req.CreateResponse(status);
            await deny.WriteStringAsync("Unauthorized.");
            return deny;
        }

        try
        {
            var ct = req.FunctionContext.CancellationToken;
            var purged = await _repo.PurgeExpiredAsync(ct);  // make sure your repo exposes this
            var ok = req.CreateResponse(HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new { purged });
            return ok;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Purge failed.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Purge failed.");
            return err;
        }
    }
}
