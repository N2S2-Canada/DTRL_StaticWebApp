// API/Functions/VideosInspect.cs
using System.Net;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace API.Functions;

public sealed class VideosInspect
{
    private readonly ILogger<VideosInspect> _log;

    public VideosInspect(ILogger<VideosInspect> log) => _log = log;

    [Function("VideosInspect")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
        FunctionContext ctx)
    {
        var res = req.CreateResponse();
        try
        {
            // ----- Read query -----
            var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            // Accept both "code" and "cc" to dodge any special handling of ?code=
            var code = q["code"] ?? q["cc"];
            var path = q["path"];

            // ----- Env sanity -----
            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var defaultFolder = Environment.GetEnvironmentVariable("FolderPath") ?? string.Empty;

            // Storage bits
            var tableName = Environment.GetEnvironmentVariable("CustomerContentTableName") ?? "CustomerContent";
            var cs = Environment.GetEnvironmentVariable("StorageConnectionString")
                     ?? Environment.GetEnvironmentVariable("Values:StorageConnectionString")
                     ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage");

            var diag = new
            {
                Input = new { code, path },
                SiteId = siteId,
                HasStorageConn = !string.IsNullOrWhiteSpace(cs),
                HasTenant = !string.IsNullOrWhiteSpace(tenantId),
                HasClient = !string.IsNullOrWhiteSpace(clientId),
                HasSecret = !string.IsNullOrWhiteSpace(clientSecret),
                Repo = new Dictionary<string, object?>(),
                Tried = new List<object>(),
                Success = (object?)null,
                Children = new List<object>(),
            };

            var tried = (List<object>)diag.Tried;
            var repo = (Dictionary<string, object?>)diag.Repo;
            var children = (List<object>)diag.Children;

            // ----- If a code was supplied, try the table lookup FIRST -----
            string? effectiveFolder = null;
            string? displayName = null;

            if (!string.IsNullOrWhiteSpace(code))
            {
                string partKey = "CustomerContent";
                string rowKey = code.Trim().ToUpperInvariant();

                try
                {
                    TableClient table;
                    if (!string.IsNullOrWhiteSpace(cs))
                    {
                        table = new TableClient(cs!, tableName);
                    }
                    else
                    {
                        var acct = Environment.GetEnvironmentVariable("StorageAccountUrl")
                                   ?? throw new InvalidOperationException("Missing StorageConnectionString or StorageAccountUrl.");
                        table = new TableClient(new Uri(acct), tableName, new DefaultAzureCredential());
                    }

                    await table.CreateIfNotExistsAsync();

                    var got = await table.GetEntityAsync<TableEntity>(partKey, rowKey, select: null, cancellationToken: ctx.CancellationToken);
                    var e = got.Value;

                    // Echo exactly what's on that row
                    var raw = e.Keys.ToDictionary(k => k, k => e[k]?.ToString());
                    repo["RowFound"] = true;
                    repo["PartitionKey"] = e.PartitionKey;
                    repo["RowKey"] = e.RowKey;
                    repo["Timestamp"] = e.Timestamp?.ToString("o");
                    repo["Properties"] = raw;

                    displayName = ReadString(e, "DisplayName");
                    effectiveFolder = ReadString(e, "SharePath");

                    // Safe read KeepAliveMonths (never throws)
                    var keep = ReadIntFlexible(e, "KeepAliveMonths", 0);
                    repo["KeepAliveMonths"] = keep;

                    if (keep > 0 && e.Timestamp is DateTimeOffset ts)
                    {
                        var expires = ts.AddMonths(keep);
                        repo["ExpiresOn"] = expires.ToString("o");
                        repo["IsExpired"] = DateTimeOffset.UtcNow > expires;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    repo["RowFound"] = false;
                    repo["Error"] = "Not found";
                }
                catch (Exception ex)
                {
                    repo["RowFound"] = false;
                    repo["Error"] = ex.GetType().Name + ": " + ex.Message;

                    res.StatusCode = HttpStatusCode.OK;
                    await res.WriteStringAsync(JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true }));
                    return res;
                }
            }

            // ----- Decide which folder to probe -----
            var folderPath = !string.IsNullOrWhiteSpace(path) ? path : (effectiveFolder ?? defaultFolder);
            folderPath = (folderPath ?? "").Replace("\\", "/").TrimStart('/');

            // ----- Graph probe -----
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            var drive = await graph.Sites[siteId].Drive.GetAsync();
            var candidates = BuildCandidates(folderPath);
            var encoded = candidates.Select(EncodeSegments).ToList();
            foreach (var enc in encoded)
            {
                if (!candidates.Contains(enc, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(enc);
            }

            DriveItem? folder = null;
            foreach (var cand in candidates)
            {
                try
                {
                    var f = await graph.Drives[drive!.Id!].Root.ItemWithPath(cand).GetAsync();
                    if (f is not null)
                    {
                        folder = f;
                        tried.Add(new { Path = cand, Http = 200, Error = (string?)null, ChildrenCount = (int?)null });
                        break;
                    }
                }
                catch (ServiceException sx) when ((int?)sx.ResponseStatusCode == 404)
                {
                    tried.Add(new { Path = cand, Http = 404, Error = "NotFound", ChildrenCount = (int?)null });
                }
                catch (ServiceException sx)
                {
                    tried.Add(new { Path = cand, Http = (int?)sx.ResponseStatusCode, Error = sx.Message, ChildrenCount = (int?)null });
                }
                catch (Exception ex)
                {
                    tried.Add(new { Path = cand, Http = (int?)null, Error = ex.Message, ChildrenCount = (int?)null });
                }
            }

            if (folder is null)
            {
                res.StatusCode = HttpStatusCode.OK;
                await res.WriteStringAsync(JsonSerializer.Serialize(diag, new JsonSerializerOptions { WriteIndented = true }));
                return res;
            }

            var kids = await graph.Drives[drive!.Id!].Items[folder.Id].Children.GetAsync();
            var list = kids?.Value ?? new List<DriveItem>();

            foreach (var it in list)
            {
                children.Add(new
                {
                    it.Name,
                    it.WebUrl,
                    IsFile = it.File is not null,
                    Ext = it.File?.MimeType
                });
            }

            var success = new { path = folderPath, count = list.Count, title = displayName };
            var final = new
            {
                diag.Input,
                diag.SiteId,
                diag.HasStorageConn,
                diag.HasTenant,
                diag.HasClient,
                diag.HasSecret,
                Repo = diag.Repo,
                Tried = diag.Tried,
                Success = success,
                Children = diag.Children
            };

            res.StatusCode = HttpStatusCode.OK;
            await res.WriteStringAsync(JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = true }));
            return res;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "VideosInspect failed.");
            res.StatusCode = HttpStatusCode.InternalServerError;
            await res.WriteStringAsync("Internal Error");
            return res;
        }
    }

    // ---------------- helpers ----------------

    private static string? ReadString(TableEntity e, string name)
        => e.TryGetValue(name, out var v) ? v?.ToString() : null;

    private static int ReadIntFlexible(TableEntity e, string name, int def = 0)
    {
        if (!e.TryGetValue(name, out var v) || v is null) return def;
        return v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string s when int.TryParse(s, out var n) => n,
            _ => def
        };
    }

    private static List<string> BuildCandidates(string raw)
    {
        var p = (raw ?? "").Replace("\\", "/").TrimStart('/');
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
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString);
        return string.Join("/", parts);
    }
}
