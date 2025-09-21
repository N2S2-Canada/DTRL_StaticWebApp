// API/Functions/GetVideos.cs
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web; // HttpUtility
using API.Services;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace API.Functions
{
    public class GetVideos
    {
        private readonly ILogger<GetVideos> _logger;
        private readonly ICustomerContentRepository _customerRepo;

        public GetVideos(ILogger<GetVideos> logger, ICustomerContentRepository customerRepo)
        {
            _logger = logger;
            _customerRepo = customerRepo;
        }

        private static string ToFriendlyName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            var withSpaces = rawName.Replace("_", " ").Replace("-", " ");
            withSpaces = Regex.Replace(withSpaces, @"\b(final|draft|copy|edit|v\d+|\d{2,})\b", "", RegexOptions.IgnoreCase);
            withSpaces = Regex.Replace(withSpaces, @"\s+", " ").Trim();

            return Regex.Replace(withSpaces, @"\b\w+\b", m =>
            {
                var word = m.Value;
                if (word.ToUpperInvariant() == word) return word; // preserve acronyms
                return char.ToUpper(word[0]) + word[1..].ToLower();
            });
        }

        private sealed class VideosResponse
        {
            public string? Title { get; set; }
            public List<SharedModels.Video> Items { get; set; } = new();
        }

        [Function("GetVideos")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos")] HttpRequestData req,
            FunctionContext context)
        {
            var res = req.CreateResponse();

            try
            {
                // Optional ?code=ABCDE
                var q = HttpUtility.ParseQueryString(req.Url.Query);
                var code = q["code"];

                var tenantId = Environment.GetEnvironmentVariable("TenantId");
                var clientId = Environment.GetEnvironmentVariable("ClientId");
                var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                var siteId = Environment.GetEnvironmentVariable("SiteId");
                var defaultFolderPath = Environment.GetEnvironmentVariable("FolderPath");

                string folderPath = defaultFolderPath ?? string.Empty;
                string? displayName = null;

                if (!string.IsNullOrWhiteSpace(code))
                {
                    if (!Regex.IsMatch(code, "^[A-Za-z0-9]{5}$"))
                    {
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteStringAsync("Invalid code format.");
                        return bad;
                    }

                    var match = await _customerRepo.GetByCodeAsync(code, req.FunctionContext.CancellationToken);
                    if (match is null || string.IsNullOrWhiteSpace(match.SharePath))
                    {
                        var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                        await notFound.WriteStringAsync("This code is invalid or has expired.");
                        return notFound;
                    }

                    folderPath = match.SharePath.Trim();
                    displayName = string.IsNullOrWhiteSpace(match.DisplayName) ? null : match.DisplayName;
                }

                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                List<DriveItem> children;

                if (!string.IsNullOrWhiteSpace(folderPath) &&
                    folderPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Sharing link path
                    var urlSafe = Convert.ToBase64String(Encoding.UTF8.GetBytes(folderPath))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    var shareId = "u!" + urlSafe;

                    var sharedItem = await graphClient.Shares[shareId].DriveItem.GetAsync();
                    if (sharedItem is null)
                    {
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteStringAsync("Shared item not found.");
                        return nf;
                    }

                    if (sharedItem.Folder is null)
                    {
                        children = new List<DriveItem> { sharedItem }; // single file link
                    }
                    else
                    {
                        var driveId = sharedItem.ParentReference?.DriveId
                                      ?? throw new InvalidOperationException("Missing DriveId on shared item");
                        var folderId = sharedItem.Id!;
                        var resChildren = await graphClient.Drives[driveId].Items[folderId].Children.GetAsync(rc =>
                        {
                            rc.QueryParameters.Expand = new[] { "listItem" };
                        });
                        children = resChildren?.Value?.ToList() ?? new List<DriveItem>();
                    }
                }
                else
                {
                    // Library-relative path
                    var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                    if (drive == null)
                    {
                        _logger.LogError("Drive not found for siteId: {SiteId}", siteId);
                        res.StatusCode = HttpStatusCode.InternalServerError;
                        await res.WriteStringAsync("Drive not found.");
                        return res;
                    }

                    DriveItem? folder = null;
                    string? tried1 = null, tried2 = null;

                    // Normalize path
                    var candidate = (folderPath ?? "").Replace('\\', '/').Trim().Trim('/');

                    // Try as given
                    tried1 = candidate;
                    try
                    {
                        folder = string.IsNullOrEmpty(candidate)
                            ? await graphClient.Drives[drive.Id!].Root.GetAsync()
                            : await graphClient.Drives[drive.Id!].Root.ItemWithPath(candidate).GetAsync();
                    }
                    catch (Exception ex1)
                    {
                        _logger.LogWarning(ex1, "ItemWithPath failed for '{Path}' (first attempt).", tried1);
                    }

                    // If not found, try with "Shared Documents/" prefix
                    if (folder is null && !string.IsNullOrEmpty(candidate) &&
                        !candidate.StartsWith("Shared Documents/", StringComparison.OrdinalIgnoreCase))
                    {
                        tried2 = $"Shared Documents/{candidate}";
                        try
                        {
                            folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(tried2).GetAsync();
                        }
                        catch (Exception ex2)
                        {
                            _logger.LogWarning(ex2, "ItemWithPath failed for '{Path}' (fallback attempt).", tried2);
                        }
                    }

                    if (folder is null)
                    {
                        var nf = req.CreateResponse(HttpStatusCode.NotFound);
                        await nf.WriteStringAsync("Folder not found.");
                        _logger.LogWarning("Folder not found. Tried: '{T1}' and '{T2}'.", tried1, tried2);
                        return nf;
                    }

                    var resChildren = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
                    {
                        rc.QueryParameters.Expand = new[] { "listItem" };
                    });
                    children = resChildren?.Value?.ToList() ?? new List<DriveItem>();
                }

                var allowedVideoExtensions = new[] { ".mp4" };
                var allowedPhotoExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

                var videoTasks = (children ?? new List<DriveItem>())
                    .Where(i => i?.File != null && i?.Name != null &&
                           (allowedVideoExtensions.Contains(Path.GetExtension(i.Name), StringComparer.OrdinalIgnoreCase) ||
                            allowedPhotoExtensions.Contains(Path.GetExtension(i.Name), StringComparer.OrdinalIgnoreCase)))
                    .Select(async i =>
                    {
                        var categories = new List<string>();
                        if (i.ListItem?.Fields?.AdditionalData != null &&
                            i.ListItem.Fields.AdditionalData.TryGetValue("Category", out var categoryObj) &&
                            categoryObj != null)
                        {
                            switch (categoryObj)
                            {
                                case UntypedArray untypedArray:
                                    categories = (untypedArray.GetValue() ?? Enumerable.Empty<UntypedNode>())
                                        .OfType<UntypedString>()
                                        .Select(x => x.GetValue() ?? string.Empty)
                                        .Where(x => !string.IsNullOrWhiteSpace(x))
                                        .ToList();
                                    break;

                                case UntypedString untypedString:
                                    var s = untypedString.GetValue() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(s)) categories.Add(s);
                                    break;
                            }
                        }

                        var extension = Path.GetExtension(i.Name ?? string.Empty).ToLowerInvariant();
                        var isVideo = allowedVideoExtensions.Contains(extension);

                        string? mediaUrl = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                            ? downloadUrl?.ToString() ?? i.WebUrl
                            : i.WebUrl;

                        string thumbnailUrl = mediaUrl ?? string.Empty;

                        if (isVideo)
                        {
                            try
                            {
                                var driveIdForItem = i.ParentReference?.DriveId!;
                                var thumbs = await graphClient.Drives[driveIdForItem].Items[i.Id].Thumbnails.GetAsync();
                                thumbnailUrl = thumbs?.Value?.FirstOrDefault()?.Large?.Url ?? mediaUrl ?? string.Empty;
                            }
                            catch
                            {
                                thumbnailUrl = mediaUrl ?? string.Empty;
                            }
                        }

                        return new SharedModels.Video
                        {
                            Name = ToFriendlyName(Path.GetFileNameWithoutExtension(i.Name ?? string.Empty)),
                            Url = mediaUrl,
                            ThumbnailUrl = thumbnailUrl,
                            Categories = categories,
                            IsVideo = isVideo
                        };
                    });

                var videos = await Task.WhenAll(videoTasks);

                res.StatusCode = HttpStatusCode.OK;
                await res.WriteAsJsonAsync(new VideosResponse
                {
                    Title = displayName,  // null when browsing the default folder
                    Items = videos.ToList()
                });
                return res;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch videos/photos from SharePoint.");
                res.StatusCode = HttpStatusCode.InternalServerError;
                await res.WriteStringAsync("Error fetching videos/photos.");
                return res;
            }
        }
    }
}
