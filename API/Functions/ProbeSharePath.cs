using System.Net;
using System.Web; // HttpUtility
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using API.Services;
using API.Security; // StaticWebAppsAuth if you want to restrict

namespace API.Functions
{
    public sealed class ProbeSharePath
    {
        private readonly ILogger<ProbeSharePath> _log;
        private readonly IConfiguration _cfg;
        private readonly IHostEnvironment _env;
        private readonly ICustomerContentRepository _customerRepo;

        public ProbeSharePath(
            ILogger<ProbeSharePath> log,
            IConfiguration cfg,
            IHostEnvironment env,
            ICustomerContentRepository customerRepo)
        {
            _log = log;
            _cfg = cfg;
            _env = env;
            _customerRepo = customerRepo;
        }

        private sealed record CandidateResult(string Path, int? Status, string? Message, string? FolderId, string? WebUrl);

        private sealed class ProbeResult
        {
            public string SiteId { get; set; } = "";
            public string? DriveId { get; set; }
            public object Input { get; set; } = new { };
            public List<CandidateResult> Candidates { get; set; } = new();
            public object? Resolved { get; set; }
            public List<object> ChildrenPreview { get; set; } = new();
        }

        [Function("ProbeSharePath")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/diag")] HttpRequestData req,
            FunctionContext ctx)
        {
            // Optional: lock this down to admins/x-api-key in prod
            if (!StaticWebAppsAuth.IsAuthorizedAdmin(req, _cfg, _env))
            {
                var deny = req.CreateResponse(StaticWebAppsAuth.HasPrincipal(req) ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
                await deny.WriteStringAsync("Unauthorized.");
                return deny;
            }

            var q = HttpUtility.ParseQueryString(req.Url.Query);
            var code = q["code"];
            var rawPath = q["path"];

            if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(rawPath))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                await bad.WriteStringAsync("Provide ?code=ABCDE or ?path=<relative-sharepoint-path>.");
                return bad;
            }

            var siteId = _cfg["SiteId"] ?? "";
            var tenantId = _cfg["TenantId"];
            var clientId = _cfg["ClientId"];
            var clientSecret = _cfg["ClientSecret"];

            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            var result = new ProbeResult { SiteId = siteId };

            try
            {
                var drive = await graph.Sites[siteId].Drive.GetAsync();
                result.DriveId = drive?.Id;

                // Work out the raw path
                string? fromTableName = null;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    code = code.Trim().ToUpperInvariant();
                    var row = await _customerRepo.GetByCodeAsync(code, ctx.CancellationToken);
                    if (row is null)
                    {
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteStringAsync("Code not found/expired.");
                        return nf;
                    }
                    rawPath = row.SharePath;
                    fromTableName = row.DisplayName;
                    result.Input = new { code, tableSharePath = rawPath, displayName = fromTableName };
                }
                else
                {
                    result.Input = new { path = rawPath };
                }

                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("The path to probe is empty.");
                    return bad;
                }

                var candidates = BuildCandidates(rawPath!);
                // Also try URL-encoded segments
                candidates.AddRange(candidates.Select(EncodeSegments).Distinct(StringComparer.OrdinalIgnoreCase));

                DriveItem? resolved = null;
                string? resolvedPath = null;

                foreach (var cand in candidates)
                {
                    try
                    {
                        var folder = await graph.Drives[result.DriveId!].Root.ItemWithPath(cand).GetAsync();
                        if (folder is not null)
                        {
                            resolved = folder;
                            resolvedPath = cand;
                            result.Candidates.Add(new CandidateResult(cand, 200, "OK", folder.Id, folder.WebUrl));
                            break;
                        }

                        result.Candidates.Add(new CandidateResult(cand, 404, "null folder", null, null));
                    }
                    catch (ServiceException sx)
                    {
                        result.Candidates.Add(new CandidateResult(cand, (int?)sx.ResponseStatusCode, sx.Message, null, null));
                    }
                    catch (Exception ex)
                    {
                        result.Candidates.Add(new CandidateResult(cand, 500, ex.Message, null, null));
                    }
                }

                if (resolved is not null)
                {
                    result.Resolved = new
                    {
                        path = resolvedPath,
                        id = resolved.Id,
                        webUrl = resolved.WebUrl
                    };

                    // Show a preview of first few children to confirm we’re in the right place
                    var children = await graph.Drives[result.DriveId!].Items[resolved.Id].Children.GetAsync();
                    foreach (var c in (children?.Value ?? new List<DriveItem>()).Take(10))
                    {
                        result.ChildrenPreview.Add(new
                        {
                            name = c.Name,
                            isVideo = c.File != null && string.Equals(Path.GetExtension(c.Name ?? ""), ".mp4", StringComparison.OrdinalIgnoreCase),
                            webUrl = c.WebUrl
                        });
                    }
                }

                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteAsJsonAsync(result);
                return ok;
            }
            catch (ServiceException sx)
            {
                _log.LogError(sx, "Graph error in ProbeSharePath. Http={Http}", (int?)sx.ResponseStatusCode);
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Graph error.");
                return err;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ProbeSharePath failed.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Internal error.");
                return err;
            }
        }

        // Candidate shapes from a raw path
        private static List<string> BuildCandidates(string raw)
        {
            var list = new List<string>();
            var p = (raw ?? "").Replace("\\", "/").Trim();
            if (string.IsNullOrWhiteSpace(p)) return list;

            var noLead = p.TrimStart('/');

            void Add(string s)
            {
                if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s, StringComparer.OrdinalIgnoreCase))
                    list.Add(s);
            }

            // As-entered (no leading slash)
            Add(noLead);
            // Also with leading slash
            Add("/" + noLead);

            // Common library prefixes
            if (!noLead.StartsWith("Shared Documents/", StringComparison.OrdinalIgnoreCase))
            {
                Add("Shared Documents/" + noLead);
                Add("/Shared Documents/" + noLead);
            }
            if (!noLead.StartsWith("Documents/", StringComparison.OrdinalIgnoreCase))
            {
                Add("Documents/" + noLead);
                Add("/Documents/" + noLead);
            }

            return list;
        }

        // Encode each segment (spaces etc.), keep slashes
        private static string EncodeSegments(string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }
    }
}
