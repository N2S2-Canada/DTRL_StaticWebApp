// API/Functions/VideosInspect.cs
using System.Net;
using System.Text.RegularExpressions;
using System.Web; // HttpUtility
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using API.Services; // ICustomerContentRepository

namespace API.Functions;

public sealed class VideosInspect
{
    private readonly ILogger<VideosInspect> _log;
    private readonly ICustomerContentRepository _repo;

    public VideosInspect(ILogger<VideosInspect> log, ICustomerContentRepository repo)
    {
        _log = log;
        _repo = repo;
    }

    [Function("VideosInspect")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
        FunctionContext ctx)
    {
        var ct = ctx.CancellationToken;

        var dbg = new List<string>();
        void Trace(string msg)
        {
            var line = $"[{DateTimeOffset.UtcNow:HH:mm:ss}] {msg}";
            dbg.Add(line);
            _log.LogInformation(msg);
        }

        var res = req.CreateResponse();
        try
        {
            // -------- Inputs --------
            var q = HttpUtility.ParseQueryString(req.Url.Query);
            var code = q["code"];
            var path = q["path"];

            Trace($"INPUT code='{(code ?? "(none)")}', path='{(path ?? "(none)")}'");

            // -------- Env --------
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var storageConn = Environment.GetEnvironmentVariable("StorageConnectionString");

            Trace($"ENV hasTenant={tenantId is not null}, hasClient={clientId is not null}, hasSecret={(clientSecret is not null)}, hasStorageConn={(storageConn is not null)}");

            string? effectivePath = path;
            string? title = null;

            // -------- If code provided, look it up via repo (DTO, not TableEntity) --------
            object? repoDump = null;
            if (!string.IsNullOrWhiteSpace(code))
            {
                var normalized = code.Trim().ToUpperInvariant();
                if (!Regex.IsMatch(normalized, "^[A-Z0-9]{5}$"))
                {
                    Trace("Code format invalid.");
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteStringAsync("Invalid code format. Expected 5 alphanumeric characters.");
                    return bad;
                }

                Trace($"Repo lookup for code '{normalized}'…");
                var row = await _repo.GetByCodeAsync(normalized, ct);
                if (row is null || string.IsNullOrWhiteSpace(row.SharePath))
                {
                    Trace("Repo: no row or SharePath missing.");
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    await nf.WriteStringAsync("This code is invalid or has expired.");
                    return nf;
                }

                // Build a safe dump using only DTO fields your model exposes
                int months = Math.Max(0, row.KeepAliveMonths); // KeepAliveMonths is int
                DateTimeOffset? expires = months > 0
                    ? row.CreatedOn.AddMonths(months)
                    : (DateTimeOffset?)null;
                var isExpired = expires.HasValue && expires.Value < DateTimeOffset.UtcNow;

                repoDump = new
                {
                    RowFound = true,
                    Code = row.Code,                   // DTO property
                    row.DisplayName,
                    row.SharePath,
                    row.KeepAliveMonths,
                    row.CreatedOn,
                    ExpiresOn = expires,
                    IsExpired = isExpired
                };

                title = string.IsNullOrWhiteSpace(row.DisplayName) ? null : row.DisplayName;
                effectivePath = row.SharePath;
                Trace($"Repo OK. Using SharePath='{effectivePath}', DisplayName='{title ?? "(none)"}'");
            }
            else
            {
                Trace("No code provided; using 'path' query directly.");
            }

            // -------- Graph auth --------
            Trace("Creating Graph client…");
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            Trace($"Resolving drive for SiteId='{siteId}'…");
            var drive = await graph.Sites[siteId].Drive.GetAsync();
            if (drive is null)
            {
                Trace("Drive not found.");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteStringAsync("Drive not found for site.");
                return err;
            }
            Trace($"Drive OK. DriveId={drive.Id}");

            // -------- Candidate paths --------
            var tried = new List<object>();
            DriveItem? folder = null;

            if (string.IsNullOrWhiteSpace(effectivePath))
            {
                Trace("No effective path to test (empty).");
            }
            else
            {
                var candidates = BuildCandidates(effectivePath);
                var encoded = candidates.Select(EncodeSegments).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var c in encoded)
                {
                    if (!candidates.Contains(c, StringComparer.OrdinalIgnoreCase))
                        candidates.Add(c);
                }

                Trace($"Built {candidates.Count} candidate path(s).");
                foreach (var c in candidates)
                {
                    Trace($"Try: {c}");
                    try
                    {
                        var f = await graph.Drives[drive.Id!].Root.ItemWithPath(c).GetAsync();
                        folder = f;
                        Trace($"Resolved: {c}, id={f?.Id}");
                        tried.Add(new { Path = c, Http = 200, Error = (string?)null, ChildrenCount = (int?)null });
                        effectivePath = c;
                        break;
                    }
                    catch (ServiceException sx)
                    {
                        var http = (int?)sx.ResponseStatusCode;
                        Trace($"Graph error on '{c}': HTTP={(http?.ToString() ?? "n/a")}");
                        tried.Add(new { Path = c, Http = http, Error = "GraphError", ChildrenCount = (int?)null });
                    }
                    catch (Exception ex)
                    {
                        Trace($"Error on '{c}': {ex.GetType().Name}");
                        tried.Add(new { Path = c, Http = (int?)null, Error = ex.GetType().Name, ChildrenCount = (int?)null });
                    }
                }
            }

            if (folder is null)
            {
                Trace("No candidate resolved to a folder.");
                var payloadNF = new
                {
                    Input = new { code, path },
                    SiteId = siteId,
                    HasStorageConn = storageConn is not null,
                    HasTenant = tenantId is not null,
                    HasClient = clientId is not null,
                    HasSecret = clientSecret is not null,
                    Repo = repoDump ?? new { RowFound = false },
                    Tried = tried,
                    Success = (object?)null,
                    Children = Array.Empty<object>(),
                    Debug = dbg
                };

                res.StatusCode = HttpStatusCode.NotFound;
                await res.WriteAsJsonAsync(payloadNF);
                return res;
            }

            // Fetch children of the resolved folder
            Trace("Fetching children of resolved folder…");
            var children = await graph.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
            {
                rc.QueryParameters.Expand = new[] { "listItem" };
            });

            var childSummaries = (children?.Value ?? new List<DriveItem>())
                .Select(i => new
                {
                    i.Name,
                    i.WebUrl,
                    IsFile = i.File is not null,
                    Ext = i.File?.MimeType
                })
                .ToList();

            // Update the last 200 item in tried with the count
            var idx = tried.FindIndex(t => (int?)(t?.GetType().GetProperty("Http")?.GetValue(t) ?? null) == 200);
            if (idx >= 0)
            {
                var last = tried[idx];
                tried[idx] = new
                {
                    Path = last.GetType().GetProperty("Path")!.GetValue(last),
                    Http = 200,
                    Error = (string?)null,
                    ChildrenCount = childSummaries.Count
                };
            }

            Trace($"Children fetched: {childSummaries.Count}");

            var output = new
            {
                Input = new { code, path },
                SiteId = siteId,
                HasStorageConn = storageConn is not null,
                HasTenant = tenantId is not null,
                HasClient = clientId is not null,
                HasSecret = clientSecret is not null,
                Repo = repoDump ?? new { RowFound = false },
                Tried = tried,
                Success = new { path = effectivePath, count = childSummaries.Count, title },
                Children = childSummaries,
                Debug = dbg
            };

            res.StatusCode = HttpStatusCode.OK;
            await res.WriteAsJsonAsync(output);
            return res;
        }
        catch (ServiceException sx)
        {
            var http = (int?)sx.ResponseStatusCode;
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { Error = "Graph error", Http = http, Debug = dbg });
            return err;
        }
        catch (Exception ex)
        {
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { Error = ex.Message, Debug = dbg });
            return err;
        }
    }

    // ---------- helpers ----------

    private static List<string> BuildCandidates(string raw)
    {
        var p = (raw ?? "").Replace("\\", "/").Trim();
        p = p.TrimStart('/');
        var list = new List<string>();
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
