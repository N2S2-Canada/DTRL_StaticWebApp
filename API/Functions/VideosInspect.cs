// API/Functions/VideosInspect.cs
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using API.Services;

namespace API.Functions
{
    public sealed class VideosInspect
    {
        private readonly ILogger<VideosInspect> _log;
        private readonly ICustomerContentRepository _repo;

        public VideosInspect(ILogger<VideosInspect> log, ICustomerContentRepository repo)
        {
            _log = log;
            _repo = repo;
        }

        private sealed class TryResult
        {
            public string Path { get; set; } = "";
            public int? Http { get; set; }
            public string? Error { get; set; }
            public int? ChildrenCount { get; set; }
        }

        private sealed class InspectResponse
        {
            public InputDto Input { get; set; } = new();
            public string? SiteId { get; set; }
            public bool HasStorageConn { get; set; }
            public bool HasTenant { get; set; }
            public bool HasClient { get; set; }
            public bool HasSecret { get; set; }
            public string RepoLookup { get; set; } = "skipped";
            public RepoEntity? RepoEntity { get; set; }
            public string? SharePath { get; set; }
            public List<TryResult> Tried { get; set; } = new();
            public SuccessDto? Success { get; set; }
            public List<ChildDto> Children { get; set; } = new();
        }

        private sealed class InputDto { public string? code { get; set; } public string? path { get; set; } }
        private sealed class RepoEntity { public string? Code { get; set; } public string? DisplayName { get; set; } public string? SharePath { get; set; } public int? KeepAliveMonths { get; set; } }
        private sealed class SuccessDto { public string Path { get; set; } = ""; public int Count { get; set; } public string? Title { get; set; } }
        private sealed class ChildDto { public string? Name { get; set; } public string? WebUrl { get; set; } public bool IsFile { get; set; } public string? Ext { get; set; } }

        [Function("VideosInspect")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
            FunctionContext ctx)
        {
            var res = req.CreateResponse();
            var q = HttpUtility.ParseQueryString(req.Url.Query);
            var code = q["code"];
            var path = q["path"];

            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var defaultPath = Environment.GetEnvironmentVariable("FolderPath") ?? "";

            var payload = new InspectResponse
            {
                Input = new InputDto { code = code, path = path },
                SiteId = siteId,
                HasStorageConn =
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("StorageConnectionString")
                        ?? Environment.GetEnvironmentVariable("Values:StorageConnectionString")
                        ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage")),
                HasTenant = !string.IsNullOrWhiteSpace(tenantId),
                HasClient = !string.IsNullOrWhiteSpace(clientId),
                HasSecret = !string.IsNullOrWhiteSpace(clientSecret),
            };

            string sharePath = path ?? defaultPath;
            string? displayName = null;

            // ---- Table lookup (only if code was supplied) ----
            if (!string.IsNullOrWhiteSpace(code))
            {
                var norm = code.Trim().ToUpperInvariant();
                if (!Regex.IsMatch(norm, "^[A-Z0-9]{5}$"))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "Invalid code format" });
                    return bad;
                }

                try
                {
                    var row = await _repo.GetByCodeAsync(norm, ctx.CancellationToken);
                    if (row == null || string.IsNullOrWhiteSpace(row.SharePath))
                    {
                        payload.RepoLookup = "not-found";
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteAsJsonAsync(payload);
                        return nf;
                    }

                    payload.RepoLookup = "ok";
                    payload.RepoEntity = new RepoEntity
                    {
                        Code = norm,
                        DisplayName = string.IsNullOrWhiteSpace(row.DisplayName) ? null : row.DisplayName,
                        SharePath = row.SharePath,
                        KeepAliveMonths = row.KeepAliveMonths
                    };

                    sharePath = row.SharePath!;
                    displayName = row.DisplayName;
                    payload.SharePath = sharePath;
                }
                catch (Exception ex)
                {
                    // If repo throws in prod, we report it (NOT 500)
                    payload.RepoLookup = "exception: " + ex.GetType().Name + " - " + ex.Message;
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteAsJsonAsync(payload);
                    return nf;
                }
            }

            // ---- Graph probe for the resolved path (or default) ----
            try
            {
                var cred = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graph = new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });
                var drive = await graph.Sites[siteId].Drive.GetAsync();

                var seeds = BuildCandidates(sharePath);
                var encoded = seeds.Select(EncodeSegments).ToList();
                var candidates = seeds.Concat(encoded).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var c in candidates)
                {
                    try
                    {
                        var folder = await graph.Drives[drive!.Id!].Root.ItemWithPath(c).GetAsync();
                        var children = await graph.Drives[drive.Id].Items[folder.Id].Children.GetAsync();

                        payload.Tried.Add(new TryResult { Path = c, Http = 200, ChildrenCount = children?.Value?.Count });

                        payload.Success = new SuccessDto { Path = c, Count = children?.Value?.Count ?? 0, Title = displayName };
                        payload.Children = (children?.Value ?? new List<DriveItem>())
                            .Select(d => new ChildDto { Name = d.Name, WebUrl = d.WebUrl, IsFile = d.File != null, Ext = d.File?.MimeType })
                            .ToList();

                        var ok = req.CreateResponse(HttpStatusCode.OK);
                        await ok.WriteAsJsonAsync(payload);
                        return ok;
                    }
                    catch (ServiceException sx)
                    {
                        payload.Tried.Add(new TryResult { Path = c, Http = (int?)sx.ResponseStatusCode, Error = sx.Message });
                        // try next candidate
                    }
                    catch (Exception ex)
                    {
                        payload.Tried.Add(new TryResult { Path = c, Error = ex.Message });
                        // try next candidate
                    }
                }

                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteAsJsonAsync(payload);
                return notFound;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "VideosInspect failed.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { error = "internal", detail = ex.Message, payload });
                return err;
            }
        }

        private static List<string> BuildCandidates(string? raw)
        {
            var list = new List<string>();
            var p = (raw ?? "").Replace("\\", "/").Trim();
            p = p.TrimStart('/');

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
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }
    }
}
