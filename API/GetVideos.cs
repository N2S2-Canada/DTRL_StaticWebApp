using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using SharedModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace API
{
    public class GetVideos
    {
        private readonly ILogger<GetVideos> _logger;

        public GetVideos(ILogger<GetVideos> logger)
        {
            _logger = logger;
        }

        [Function("GetVideos")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos")] HttpRequestData req,
            FunctionContext context)
        {
            var response = req.CreateResponse();

            try
            {
                // Environment variables
                var tenantId = Environment.GetEnvironmentVariable("TenantId");
                var clientId = Environment.GetEnvironmentVariable("ClientId");
                var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
                var siteId = Environment.GetEnvironmentVariable("SiteId");
                var folderPath = Environment.GetEnvironmentVariable("FolderPath");

                var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                // Get site's default drive
                var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                if (drive == null)
                {
                    _logger.LogError("Drive not found for siteId: {SiteId}", siteId);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Drive not found.");
                    return response;
                }

                // Get folder by path
                var folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(folderPath).GetAsync();
                if (folder == null)
                {
                    _logger.LogError("Folder not found at path: {FolderPath}", folderPath);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Folder not found.");
                    return response;
                }

                // Get files in folder, expand listItem
                var items = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync(rc =>
                {
                    rc.QueryParameters.Expand = new[] { "listItem" };
                });

                var videos = (items?.Value ?? Enumerable.Empty<DriveItem>())
                    .Where(i => i?.File != null && i?.Name != null && i.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    .Select(i =>
                    {
                        // Convert SharePoint multi-choice Category column to List<string>
                        var categories = new List<string>();

                        if (i.ListItem?.Fields?.AdditionalData != null &&
                            i.ListItem.Fields.AdditionalData.TryGetValue("Category", out var categoryObj) &&
                            categoryObj != null)
                        {
                            switch (categoryObj)
                            {
                                case UntypedArray untypedArray:
                                    categories = (untypedArray.GetValue() ?? Enumerable.Empty<UntypedNode>())
                                        .OfType<UntypedString>()               // Only take UntypedString nodes
                                        .Select(x => x.GetValue() ?? string.Empty) // Correctly get string value
                                        .Where(x => !string.IsNullOrWhiteSpace(x))
                                        .ToList();
                                    break;

                                case UntypedString untypedString:
                                    categories.Add(untypedString.GetValue() ?? string.Empty);
                                    break;
                            }
                        }

                        _logger.LogInformation("Video {Name} categories: {Categories}", i.Name, string.Join(", ", categories));

                        return new SharedModels.Video
                        {
                            Name = i.Name,
                            Url = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                                ? downloadUrl?.ToString()
                                : i.WebUrl,
                            Categories = categories
                        };
                    }).ToList();

                response.StatusCode = System.Net.HttpStatusCode.OK;
                await response.WriteAsJsonAsync(videos);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch videos from SharePoint.");
                response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                await response.WriteStringAsync("Error fetching videos.");
                return response;
            }
        }
    }
}
