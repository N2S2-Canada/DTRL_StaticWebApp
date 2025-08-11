using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Azure.Identity;
using Microsoft.Graph.Models;

namespace API
{
    public class GetVideos
    {
        private readonly ILogger _log;

        public GetVideos(ILoggerFactory loggerFactory)
        {
            _log = loggerFactory.CreateLogger<GetVideos>();
        }

        [Function("GetVideos")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route ="videos")] HttpRequestData req,
            FunctionContext context)
        {
            // Retrieve environment variables
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
            var siteId = Environment.GetEnvironmentVariable("SiteId");
            var folderPath = Environment.GetEnvironmentVariable("FolderPath");

            var response = req.CreateResponse();

            try
            {
                var credential = new ClientSecretCredential(
                    tenantId,
                    clientId,
                    clientSecret
                );

                var graphClient = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });

                // Get site's default drive
                var drive = await graphClient.Sites[siteId].Drive.GetAsync();
                if (drive == null)
                {
                    _log.LogError("Drive not found for siteId: {siteId}", siteId);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Drive not found.");
                    return response;
                }

                // Get folder by path (relative to root)
                var folder = await graphClient.Drives[drive.Id!].Root.ItemWithPath(folderPath).GetAsync();
                if (folder == null)
                {
                    _log.LogError("Folder not found at path: {folderPath}", folderPath);
                    response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                    await response.WriteStringAsync("Folder not found.");
                    return response;
                }

                // Get files in the folder
                var items = await graphClient.Drives[drive.Id].Items[folder.Id].Children.GetAsync();

                var videos = (items?.Value ?? Enumerable.Empty<DriveItem>())
                    .Where(i => i?.File != null && i?.Name != null && i.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    .Select(i => new
                    {
                        name = i.Name,
                        url = i.AdditionalData != null && i.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out var downloadUrl)
                            ? downloadUrl?.ToString()
                            : i.WebUrl
                    });

                response.StatusCode = System.Net.HttpStatusCode.OK;
                await response.WriteAsJsonAsync(videos);
                return response;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to fetch videos from SharePoint.");
                response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                await response.WriteStringAsync("Error fetching videos.");
                return response;
            }
        }
    }
}