// API/Functions/VideosInspect.cs
using System.Net;
using System.Text.Json;
using System.Web;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace API.Functions;

public sealed partial class VideosInspect
{
    private readonly ILogger<VideosInspect> _log;
    public VideosInspect(ILogger<VideosInspect> log) => _log = log;

    private static object DumpException(Exception ex)
        => new { Type = ex.GetType().FullName, ex.Message, ex.StackTrace };

    [Function("VideosInspect")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos/inspect")] HttpRequestData req,
        FunctionContext ctx)
    {
        var res = req.CreateResponse(HttpStatusCode.OK); // always 200 so you see the JSON
        var tried = new List<object?>();
        object? success = null;
        var childrenDump = new List<object>();
        object? repoDump = null;
        object? finalError = null;

        // Inputs
        string? customerCode = null;
        string? path = null;
        try
        {
            var q = HttpUtility.ParseQueryString(req.Url.Query);
            customerCode = q["cust_code"]?.Trim();
            path = q["path"]?.Trim();
        }
        catch (Exception ex)
        {
            finalError = DumpException(ex);
        }

        // Env
        var siteId = Environment.GetEnvironmentVariable("SiteId");
        var tenantId = Environment.GetEnvironmentVariable("TenantId");
        var clientId = Environment.GetEnvironmentVariable("ClientId");
        var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
        var storageConn = Environment.GetEnvironmentVariable("StorageConnectionString");
        var defaultFolder = Environment.GetEnvironmentVariable("FolderPath") ?? "";

        // Step 1: If ?code=ABCDE is present, try to read from CustomerContent table
        string? sharePath = path;
        string? displayName = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(customerCode))
            {
                // Table name is CustomerContent; StorageConnectionString is used
                var table = new TableClient(storageConn!, "CustomerContent");
                // Use a robust query to avoid projection issues
                await foreach (var row in table.QueryAsync<TableEntity>(
                                   filter: $"PartitionKey eq 'CustomerContent' and RowKey eq '{customerCode}'",
                                   cancellationToken: ctx.CancellationToken))
                {
                    // Try to read SharePath (string)
                    sharePath = row.TryGetValue("SharePath", out var sp) ? sp?.ToString() : null;

                    // DisplayName is optional
                    displayName = row.TryGetValue("DisplayName", out var dn) ? dn?.ToString() : null;

                    // KeepAliveMonths can be int/long/string — parse defensively
                    int keep = 0;
                    if (row.TryGetValue("KeepAliveMonths", out var rawKeep) && rawKeep is not null)
                    {
                        keep = rawKeep switch
                        {
                            int i => i,
                            long l => (int)l,
                            string s when int.TryParse(s, out var k) => k,
                            _ => 0
                        };
                    }

                    // CreatedOn optional; also allow Timestamp fallback
                    DateTimeOffset created = row.TryGetValue("CreatedOn", out var co) && co is DateTimeOffset dto
                        ? dto
                        : (row.TryGetValue("Timestamp", out var ts) && ts is DateTimeOffset tsto ? tsto : DateTimeOffset.MinValue);

                    DateTimeOffset? expires = keep > 0 && created != DateTimeOffset.MinValue
                        ? created.AddMonths(Math.Max(0, keep))
                        : null;

                    var isExpired = expires.HasValue && expires.Value < DateTimeOffset.UtcNow;

                    repoDump = new
                    {
                        RowFound = true,
                        PartitionKey = row.PartitionKey,
                        RowKey = row.RowKey,
                        row.Timestamp,
                        Properties = row, // raw for sanity
                        KeepAliveMonths = keep,
                        CreatedOn = created,
                        ExpiresOn = expires,
                        IsExpired = isExpired
                    };

                    if (isExpired)
                    {
                        // Report but do not throw
                        finalError = new { Message = "Code is expired.", Code = customerCode, Expires = expires };
                    }

                    break; // we only expect one row
                }

                if (repoDump is null)
                {
                    repoDump = new { RowFound = false, Code = customerCode };
                    finalError = new { Message = "No row for code.", Code = customerCode };
                }
            }
            else
            {
                repoDump = "skipped";
            }
        }
        catch (Exception ex)
        {
            repoDump = new { Error = DumpException(ex) };
            finalError = DumpException(ex);
        }

        // Step 2: If no code or no SharePath found, fall back to ?path or default
        if (string.IsNullOrWhiteSpace(sharePath))
            sharePath = string.IsNullOrWhiteSpace(path) ? defaultFolder : path;

        // Step 3: Graph resolve & list
        try
        {
            var cred = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var graph = new GraphServiceClient(cred, new[] { "https://graph.microsoft.com/.default" });

            // Try as-is; you can add more candidates if needed
            var candidates = new List<string> { sharePath.TrimStart('/') };

            DriveItem? folder = null;
            Drive? drive = await graph.Sites[siteId].Drive.GetAsync();

            foreach (var c in candidates)
            {
                try
                {
                    var f = await graph.Drives[drive!.Id!].Root.ItemWithPath(c).GetAsync();
                    if (f is not null)
                    {
                        folder = f;
                        tried.Add(new { Path = c, Http = 200, Error = (object?)null, ChildrenCount = (int?)null });
                        break;
                    }
                    tried.Add(new { Path = c, Http = (int?)404, Error = (object?)null, ChildrenCount = (int?)null });
                }
                catch (ServiceException sx)
                {
                    tried.Add(new
                    {
                        Path = c,
                        Http = (int?)sx.ResponseStatusCode,
                        Error = new { Type = sx.GetType().FullName, sx.Message },
                        ChildrenCount = (int?)null
                    });
                }
                catch (Exception ex)
                {
                    tried.Add(new { Path = c, Http = (int?)null, Error = DumpException(ex), ChildrenCount = (int?)null });
                }
            }

            if (folder is not null)
            {
                var kids = await graph.Drives[drive!.Id!].Items[folder.Id!].Children.GetAsync();
                var list = kids?.Value?.ToList() ?? new List<DriveItem>();
                success = new { path = sharePath, count = list.Count, title = displayName };

                foreach (var k in list)
                {
                    childrenDump.Add(new
                    {
                        k.Name,
                        k.WebUrl,
                        IsFile = k.File is not null,
                        Ext = k.File?.MimeType
                    });
                }
            }
        }
        catch (Exception ex)
        {
            finalError = DumpException(ex);
        }

        var payload = new
        {
            Input = new { customerCode, path },
            SiteId = siteId,
            HasStorageConn = !string.IsNullOrWhiteSpace(storageConn),
            HasTenant = !string.IsNullOrWhiteSpace(tenantId),
            HasClient = !string.IsNullOrWhiteSpace(clientId),
            HasSecret = !string.IsNullOrWhiteSpace(clientSecret),
            Repo = repoDump,
            Tried = tried,
            Success = success,
            Children = childrenDump,
            Error = finalError
        };

        await res.WriteStringAsync(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return res;
    }
}
