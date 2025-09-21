using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using API.Services;
using API.Security;

namespace API.Functions;

public class UpsertPageText
{
    private readonly IPageTextRepository _repo;
    private readonly ILogger<UpsertPageText> _log;
    private readonly string? _apiKey;

    public UpsertPageText(IPageTextRepository repo, ILogger<UpsertPageText> log, IConfiguration cfg)
    {
        _repo = repo;
        _log = log;
        _apiKey = cfg["CmsAdminApiKey"] ?? cfg["Values:CmsAdminApiKey"];
    }

    private sealed record Row(string Key, string Content);

    [Function("UpsertPageText")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "pagetext")] HttpRequestData req)
    {
        // SWA admin role OR dev x-api-key
        var authorized = StaticWebAppsAuth.IsInRole(req, "admin");
        if (!authorized && !string.IsNullOrWhiteSpace(_apiKey)
            && req.Headers.TryGetValues("x-api-key", out var apis)
            && string.Equals(apis.FirstOrDefault(), _apiKey, StringComparison.Ordinal))
        {
            authorized = true;
        }
        if (!authorized)
        {
            var deny = req.CreateResponse(HttpStatusCode.Unauthorized);
            await deny.WriteStringAsync("Unauthorized.");
            return deny;
        }

        var rows = await req.ReadFromJsonAsync<List<Row>>() ?? new();
        var kvs = rows.Where(r => !string.IsNullOrWhiteSpace(r.Key))
                       .Select(r => new KeyValuePair<string, string>(r.Key.Trim(), r.Content ?? ""));

        await _repo.UpsertAsync(kvs);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }
}
