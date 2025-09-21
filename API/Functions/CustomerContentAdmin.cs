using System.Net;
using System.Text.Json;
using API.Security;
using API.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace API.Functions;

public sealed class CustomerContentAdmin
{
    private readonly ILogger<CustomerContentAdmin> _log;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ICustomerContentRepository _repo;

    public CustomerContentAdmin(
        ILogger<CustomerContentAdmin> log,
        IConfiguration cfg,
        IHostEnvironment env,
        ICustomerContentRepository repo)
    {
        _log = log;
        _cfg = cfg;
        _env = env;
        _repo = repo;
    }

    private bool IsAdmin(HttpRequestData req) => StaticWebAppsAuth.IsAuthorizedAdmin(req, _cfg, _env);

    // GET /api/customer-content
    [Function("ListCustomerContent")]
    public async Task<HttpResponseData> List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customer-content")] HttpRequestData req)
    {
        if (!IsAdmin(req)) return req.CreateResponse(HttpStatusCode.Unauthorized);

        var ct = req.FunctionContext.CancellationToken;
        var rows = await _repo.ListAsync(ct);

        var payload = rows.Select(r =>
        {
            var expires = r.CreatedOn.AddMonths(Math.Max(1, r.KeepAliveMonths));
            var active = DateTimeOffset.UtcNow <= expires;
            return new
            {
                Code = r.Code,
                r.DisplayName,
                r.SharePath,
                r.KeepAliveMonths,
                r.CreatedOn,
                ExpiresOn = expires,
                Active = active
            };
        });

        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(payload, cancellationToken: ct);
        return res;
    }

    private sealed record GenerateRequest(string? DisplayName, int? KeepAliveMonths);
    // POST /api/customer-content/code
    [Function("GenerateCustomerCode")]
    public async Task<HttpResponseData> Generate([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customer-content/code")] HttpRequestData req)
    {
        if (!IsAdmin(req)) return req.CreateResponse(HttpStatusCode.Unauthorized);
        var ct = req.FunctionContext.CancellationToken;

        GenerateRequest? body = null;
        try { body = await req.ReadFromJsonAsync<GenerateRequest>(cancellationToken: ct); } catch { }

        var keep = body?.KeepAliveMonths ?? 12;
        var display = body?.DisplayName;

        var entity = await _repo.CreateCodeAsync(display, keep, ct);
        var code = entity.Code;

        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(new
        {
            Code = code,
            entity.DisplayName,
            entity.KeepAliveMonths,
            entity.SharePath,
            entity.CreatedOn,
            ExpiresOn = entity.CreatedOn.AddMonths(entity.KeepAliveMonths),
            Active = true
        }, cancellationToken: ct);
        return res;
    }

    private sealed record UpsertRequest(string Code, string? DisplayName, string? SharePath, int? KeepAliveMonths);
    // POST /api/customer-content
    [Function("UpsertCustomerContent")]
    public async Task<HttpResponseData> Upsert([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customer-content")] HttpRequestData req)
    {
        if (!IsAdmin(req)) return req.CreateResponse(HttpStatusCode.Unauthorized);
        var ct = req.FunctionContext.CancellationToken;

        UpsertRequest? body;
        try { body = await req.ReadFromJsonAsync<UpsertRequest>(cancellationToken: ct); }
        catch { body = null; }
        if (body is null || string.IsNullOrWhiteSpace(body.Code))
            return await Bad(req, "Code is required.");

        var entry = await _repo.GetByCodeAsync(body.Code, ct);
        if (entry is null)
            return req.CreateResponse(HttpStatusCode.NotFound);

        entry.DisplayName = body.DisplayName ?? entry.DisplayName;
        entry.SharePath = body.SharePath ?? entry.SharePath;
        entry.KeepAliveMonths = body.KeepAliveMonths ?? entry.KeepAliveMonths;
        await _repo.UpsertAsync(entry, ct);

        return req.CreateResponse(HttpStatusCode.OK);
    }

    // DELETE /api/customer-content/{code}
    [Function("DeleteCustomerContent")]
    public async Task<HttpResponseData> Delete([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customer-content/{code}")] HttpRequestData req, string code)
    {
        if (!IsAdmin(req)) return req.CreateResponse(HttpStatusCode.Unauthorized);
        var ct = req.FunctionContext.CancellationToken;

        var ok = await _repo.DeleteAsync(code, ct);
        return req.CreateResponse(ok ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData req, string message)
    {
        var r = req.CreateResponse(HttpStatusCode.BadRequest);
        await r.WriteStringAsync(message);
        return r;
    }
}
