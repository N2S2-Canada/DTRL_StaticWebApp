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
    public class VideosInspect
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
            public object Input { get; set; } = new { };
            public string? SiteId { get; set; }
            public bool HasStorageConn { get; set; }
            public bool HasTenant { get; set; }
            public bool HasClient { get; set; }
            public bool HasSecret { get; set; }
            public string RepoLookup { get; set; } = "skipped";
            public string? SharePath { get; set; }
            public List<TryResult> Tried { get; set; } = new();
            public object? Success { get; set; }
            public List<object> Children { get; set; } = new();
        }

        [Function("VideosInspect")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
            FunctionContext ctx)
        {
            var query = HttpUtility.ParseQueryString(req.Url.Query);
            var code = query["code"];
            var path = query["path"];

            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var defaultPath = Environment.GetEnvironmentVariable("FolderPath") ?? "";

            var payload = new InspectResponse
            {
                Input = new { code, path },
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

            // --- Optional: code lookup (diagnose table path) ---
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
                    var match = await _repo.GetByCodeAsync(norm, ctx.CancellationToken);
                    if (match is null || string.IsNullOrWhiteSpace(match.SharePath))
                    {
                        payload.RepoLookup = "not-found";
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteAsJsonAsync(payload);
                        return nf;
                    }

                    payload.RepoLookup = "ok";
                    sharePath = match.SharePath!;
                    displayName = match.DisplayName;
                    payload.SharePath = sharePath;
                }
                catch (Exception ex)
                {
                    payload.RepoLookup = "exception: " + ex.GetType().Name;
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteAsJsonAsync(payload);
                    return nf;
                }
            }

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

                        payload.Tried.Add(new TryResult
                        {
                            Path = c,
                            Http = 200,
                            ChildrenCount = children?.Value?.Count
                        });

                        payload.Success = new { path = c, count = children?.Value?.Count ?? 0, title = displayName };
                        payload.Children = (children?.Value ?? new List<DriveItem>())
                            .Select(d => new { d.Name, d.WebUrl, IsFile = d.File != null, Ext = d.File?.MimeType })
                            .Cast<object>()
                            .ToList();

                        var ok = req.CreateResponse(HttpStatusCode.OK);
                        await ok.WriteAsJsonAsync(payload);
                        return ok;
                    }
                    catch (ServiceException sx)
                    {
                        payload.Tried.Add(new TryResult
                        {
                            Path = c,
                            Http = (int?)sx.ResponseStatusCode,
                            Error = sx.Message
                        });
                        // try next
                    }
                    catch (Exception ex)
                    {
                        payload.Tried.Add(new TryResult
                        {
                            Path = c,
                            Http = null,
                            Error = ex.Message
                        });
                        // try next
                    }
                }

                // None worked
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
