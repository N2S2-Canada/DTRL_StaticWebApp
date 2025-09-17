using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Text.RegularExpressions;

namespace API
{
    public class GetVideos
    {
        private readonly ILogger<GetVideos> _logger;

        public GetVideos(ILogger<GetVideos> logger)
        {
            _logger = logger;
        }

        private static string ToFriendlyName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;

            var withSpaces = rawName.Replace("_", " ").Replace("-", " ");
            withSpaces = Regex.Replace(withSpaces, @"\b(final|draft|copy|edit|v\d+|\d{2,})\b", "", RegexOptions.IgnoreCase);
            withSpaces = Regex.Replace(withSpaces, @"\s+", " ").Trim();

            return Regex.Replace(withSpaces, @"\b\w+\b", match =>
            {
                var word = match.Value;
                if (word.ToUpperInvariant() == word) return word; // preserve acronyms
                return char.ToUpper(word[0]) + word.Substring(1).ToLower();
            });
        }

        [Function("GetVideos")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos")] HttpRequestData req,
            FunctionContext context)
        {
            var response = req.CreateResponse();

            try
            {
                var tenantId = Environment.GetEnvironmentVariable("TenantId");
                var clientId = Environment.GetEnvironmentVariable("ClientId");
                var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                var siteId = Environment.GetEnvironmentVariable("SiteId");
                var folderPath = Environment.GetEnvironmentVariable("FolderPath");

                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                if (drive == null)
                {
                    _logger.LogError("Drive not found for siteId: {SiteId}", siteId);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Drive not found.");
                    return response;
                }

                var folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(folderPath).GetAsync();
                if (folder == null)
                {
                    _logger.LogError("Folder not found at path: {FolderPath}", folderPath);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Folder not found.");
                    return response;
                }

                var items = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
                {
                    rc.QueryParameters.Expand = new[] { "listItem" };
                });

                var allowedVideoExtensions = new[] { ".mp4" };
                var allowedPhotoExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif" };

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

                        // Use download URL if available
                        string? mediaUrl = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                            ? downloadUrl?.ToString() ?? i.WebUrl
                            : i.WebUrl;

                        string thumbnailUrl = mediaUrl ?? string.Empty;

                        if (isVideo)
                        {
                            try
                            {
                                var thumbs = await graphClient.Drives[drive.Id].Items[i.Id].Thumbnails.GetAsync();
                                // Use the LARGE thumbnail if available
                                thumbnailUrl = thumbs?.Value?.FirstOrDefault()?.Large?.Url ?? mediaUrl ?? string.Empty;
                            }
                            catch
                            {
                                thumbnailUrl = mediaUrl ?? string.Empty; // fallback
                            }
                        }

                        return new SharedModels.Video
                        {
                            Name = ToFriendlyName(Path.GetFileNameWithoutExtension(i.Name ?? string.Empty)),
                            Url = mediaUrl,
                            ThumbnailUrl = thumbnailUrl ?? string.Empty,
                            Categories = categories,
                            IsVideo = isVideo
                        };
                    });

                var videos = await Task.WhenAll(videoTasks);

                response.StatusCode = System.Net.HttpStatusCode.OK;
                await response.WriteAsJsonAsync(videos);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch videos or photos from SharePoint.");
                response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                await response.WriteStringAsync("Error fetching videos/photos.");
                return response;
            }
        }
    }
}
