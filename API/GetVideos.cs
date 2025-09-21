using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Net;
using System.Text.RegularExpressions;
using System.Web; // for HttpUtility
using API.Services; // ICustomerContentRepository

namespace API
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
                    // 5-char alphanumeric validation
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

                    folderPath = match.SharePath;
                    displayName = string.IsNullOrWhiteSpace(match.DisplayName) ? null : match.DisplayName;
                }

                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                if (drive == null)
                {
                    _logger.LogError("Drive not found for siteId: {SiteId}", siteId);
                    res.StatusCode = HttpStatusCode.InternalServerError;
                    await res.WriteStringAsync("Drive not found.");
                    return res;
                }

                var folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(folderPath).GetAsync();
                if (folder == null)
                {
                    _logger.LogWarning("Folder not found at path: {FolderPath}", folderPath);
                    res.StatusCode = HttpStatusCode.NotFound;
                    await res.WriteStringAsync("Folder not found.");
                    return res;
                }

                var items = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
                {
                    rc.QueryParameters.Expand = new[] { "listItem" };
                });

                var allowedVideoExtensions = new[] { ".mp4" };
                var allowedPhotoExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

                var videoTasks = (items?.Value ?? Enumerable.Empty<DriveItem>())
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
                                    categories.Add(untypedString.GetValue() ?? string.Empty);
                                    break;
                            }
                        }

                        var extension = Path.GetExtension(i.Name ?? string.Empty).ToLowerInvariant();
                        var isVideo = allowedVideoExtensions.Contains(extension);

                        // Prefer the one-time download URL if available
                        string? mediaUrl = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                            ? downloadUrl?.ToString() ?? i.WebUrl
                            : i.WebUrl;

                        string thumbnailUrl = mediaUrl ?? string.Empty;

                        if (isVideo)
                        {
                            try
                            {
                                var thumbs = await graphClient.Drives[drive.Id].Items[i.Id].Thumbnails.GetAsync();
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
