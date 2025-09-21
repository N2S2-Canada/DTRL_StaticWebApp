// API/Functions/VideosInspect.cs  (DROP-IN REPLACEMENT FOR YOUR DIAG FUNCTION)
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using API.Services;

namespace API.Functions;

public sealed class VideosInspect
{
    private readonly ILogger<VideosInspect> _log;
    private readonly ICustomerContentRepository _customerRepo;

    public VideosInspect(ILogger<VideosInspect> log, ICustomerContentRepository customerRepo)
    {
        _log = log;
        _customerRepo = customerRepo;
    }

    private sealed record ChildPreview(string Name, string? WebUrl, bool IsFile, string? Ext);
    private sealed record TryResult(string Path, int? Http, string? Error, int? ChildrenCount);

    [Function("VideosInspect")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
        FunctionContext ctx)
    {
        var res = req.CreateResponse();
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        res.Headers.Add("Cache-Control", "no-store");

        try
        {
            var q = HttpUtility.ParseQueryString(req.Url.Query);
            var code = q["code"];
            var path = q["path"];

            if (!string.IsNullOrWhiteSpace(code))
            {
                var norm = code.Trim().ToUpperInvariant();
                if (!Regex.IsMatch(norm, "^[A-Z0-9]{5}$"))
                {
                    res.StatusCode = HttpStatusCode.BadRequest;
                    await res.WriteStringAsync("""{"error":"Invalid code format."}""");
                    return res;
                }

                var row = await _customerRepo.GetByCodeAsync(norm, ctx.CancellationToken);
                if (row is null || string.IsNullOrWhiteSpace(row.SharePath))
                {
                    res.StatusCode = HttpStatusCode.NotFound;
                    await res.WriteStringAsync("""{"error":"Code not found or expired."}""");
                    return res;
                }

                path = row.SharePath!;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                res.StatusCode = HttpStatusCode.BadRequest;
                await res.WriteStringAsync("""{"error":"Provide ?path=... or ?code=ABCDE"}""");
                return res;
            }

            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var siteId = Environment.GetEnvironmentVariable("SiteId");

            if (string.IsNullOrWhiteSpace(tenantId) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(clientSecret) ||
                string.IsNullOrWhiteSpace(siteId))
            {
                res.StatusCode = HttpStatusCode.InternalServerError;
                await res.WriteStringAsync("""{"error":"Missing env vars TenantId/ClientId/ClientSecret/SiteId."}""");
                return res;
            }

            var cred = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph = new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });

            Drive? drive;
            try
            {
                drive = await graph.Sites[siteId].Drive.GetAsync();
                if (drive is null) throw new Exception("Drive is null.");
            }
            catch (ServiceException sx)
            {
                _log.LogError(sx, "Failed to resolve Site.Drive. Http={Http}", (int?)sx.ResponseStatusCode);
                res.StatusCode = HttpStatusCode.InternalServerError;
                await res.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to resolve Site.Drive", http = (int?)sx.ResponseStatusCode }));
                return res;
            }

            var seed = BuildCandidates(path);
            var candidates = seed
                .Concat(seed.Select(EncodeSegments))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tries = new List<TryResult>();
            List<ChildPreview>? childrenPreview = null;
            string? successPath = null;

            foreach (var cand in candidates)
            {
                try
                {
                    var folder = await graph.Drives[drive.Id!].Root.ItemWithPath(cand).GetAsync();
                    var kids = await graph.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
                    {
                        rc.QueryParameters.Top = 25;
                    });

                    successPath = cand;
                    childrenPreview = (kids?.Value ?? new List<DriveItem>())
                        .Select(k => new ChildPreview(
                            Name: k.Name ?? "",
                            WebUrl: k.WebUrl,
                            IsFile: k.File != null,
                            Ext: k.File?.MimeType
                        ))
                        .ToList();

                    tries.Add(new TryResult(cand, 200, null, childrenPreview.Count));
                    break;
                }
                catch (ServiceException sx)
                {
                    var http = (int?)sx.ResponseStatusCode;
                    tries.Add(new TryResult(cand, http, "Graph service error", null));
                }
                catch (Exception ex)
                {
                    tries.Add(new TryResult(cand, null, ex.GetType().Name + ": " + ex.Message, null));
                }
            }

            var payload = new
            {
                input = new { code, path },
                siteId,
                tried = tries,
                success = successPath is not null ? new { path = successPath, count = childrenPreview?.Count ?? 0 } : null,
                children = childrenPreview
            };

            res.StatusCode = successPath is null ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            await res.WriteStringAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return res;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "VideosInspect failed.");
            res.StatusCode = HttpStatusCode.InternalServerError;
            await res.WriteStringAsync(JsonSerializer.Serialize(new { error = "Internal Error", detail = ex.Message }));
            return res;
        }
    }

    private static List<string> BuildCandidates(string raw)
    {
        var list = new List<string>();
        var p = (raw ?? "").Replace("\\", "/").Trim().TrimStart('/');
        if (!string.IsNullOrWhiteSpace(p))
        {
            list.Add(p);
            if (!p.StartsWith("Shared Documents/", StringComparison.OrdinalIgnoreCase))
                list.Add("Shared Documents/" + p);
            if (!p.StartsWith("Documents/", StringComparison.OrdinalIgnoreCase))
                list.Add("Documents/" + p);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string EncodeSegments(string path)
    {
        var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
        return string.Join("/", parts);
    }
}
