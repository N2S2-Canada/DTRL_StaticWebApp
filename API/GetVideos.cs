using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Azure.Identity;
using Microsoft.Graph.Models;

namespace SharePointVideoProxy
{
    public static class GetVideos
    {
        [FunctionName("GetVideos")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "videos")] HttpRequest req,
            ILogger log)
        {
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var folderPath = Environment.GetEnvironmentVariable("FolderPath");

            var credential = new ClientSecretCredential(
                tenantId,
                clientId,
                clientSecret
            );

            var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

            try
            {
                // Get site's default drive
                // Get site's default drive
                var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                if (drive == null)
                {
                    log.LogError("Drive not found for siteId: {siteId}", siteId);
                    return new StatusCodeResult(500);
                }

                // Get folder by path (relative to root)
                var folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(folderPath).GetAsync();
                if (folder == null)
                {
                    log.LogError("Folder not found at path: {folderPath}", folderPath);
                    return new StatusCodeResult(500);
                }

                // Get files in the folder
                var items = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync();

                // Replace the following lines:
                // var videos = items.Value
                //     .Where(i => i.File != null && i.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                //     .Select(i => new
                //     {
                //         name = i.Name,
                //         url = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                //             ? downloadUrl?.ToString()
                //             : i.WebUrl
                //     });

                var videos = (items?.Value ?? Enumerable.Empty<DriveItem>())
    .Where(i => i?.File != null && i?.Name != null && i.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
    .Select(i => new
    {
        name = i.Name,
        url = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
            ? downloadUrl?.ToString()
            : i.WebUrl
    });

                return new OkObjectResult(videos);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to fetch videos from SharePoint.");
                return new StatusCodeResult(500);
            }
        }
    }
}