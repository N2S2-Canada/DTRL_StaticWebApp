using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

public sealed class GetBlobUploadSas
{
    private readonly IConfiguration _cfg;

    public GetBlobUploadSas(IConfiguration cfg) => _cfg = cfg;

    private sealed record SasRequest(string? Container, string? FileName, string? ContentType, bool? Overwrite);
    private sealed record SasResponse(string UploadUrl, string BlobUrl, DateTimeOffset ExpiresOn);

    [Function("GetBlobUploadSas")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "blob/sas")] HttpRequestData req)
    {
        // Prod auth: protected by SWA route rule (admin only).
        // Local dev: you can additionally allow x-api-key (optional).
        var adminKey = _cfg["ServicesAdminApiKey"] ?? _cfg["CmsAdminApiKey"];
        if (!string.IsNullOrEmpty(adminKey))
        {
            if (!req.Headers.TryGetValues("x-api-key", out var vals) || !vals.Any(v => v == adminKey))
            {
                var unauthorized = req.CreateResponse(HttpStatusCode.Unauthorized);
                await unauthorized.WriteStringAsync("Missing or invalid x-api-key.");
                return unauthorized;
            }
        }

        // Read JSON body
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var r = JsonSerializer.Deserialize<SasRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? new SasRequest(null, null, null, null);

        var containerName = string.IsNullOrWhiteSpace(r.Container) ? (_cfg["ImagesContainer"] ?? "media") : r.Container!;
        var fileName = r.FileName ?? $"upload-{Guid.NewGuid():N}";
        var overwrite = r.Overwrite ?? false;

        // Blob service client: prefer connection string (SWA Free),
        // fallback to account URL + DefaultAzureCredential (if you later use MI or SP).
        BlobServiceClient svc;
        var cs = _cfg["StorageConnectionString"] ?? _cfg["Values:StorageConnectionString"] ?? _cfg["AzureWebJobsStorage"];
        if (!string.IsNullOrWhiteSpace(cs))
        {
            svc = new BlobServiceClient(cs);
        }
        else
        {
            var accountUrl = _cfg["StorageAccountUrl"]
                ?? throw new InvalidOperationException("Provide StorageConnectionString or StorageAccountUrl.");
            svc = new BlobServiceClient(new Uri(accountUrl), new DefaultAzureCredential());
        }

        var container = svc.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(Azure.Storage.Blobs.Models.PublicAccessType.Blob); // public read

        var blob = container.GetBlobClient(fileName);

        // Build SAS (prefer account key path if available)
        Uri sasUri;
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(20);

        if (blob.CanGenerateSasUri) // true when you used connection string (has key)
        {
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = fileName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = expires
            };
            sasBuilder.SetPermissions(overwrite == true
                ? BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read | BlobSasPermissions.Delete
                : BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read);

            sasUri = blob.GenerateSasUri(sasBuilder);
        }
        else
        {
            // User delegation SAS (AAD) path
            var key = await svc.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-2), expires);
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = fileName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = expires
            };
            sasBuilder.SetPermissions(overwrite == true
                ? BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read | BlobSasPermissions.Delete
                : BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read);

            sasUri = new Uri(blob.Uri + "?" + sasBuilder.ToSasQueryParameters(key, svc.AccountName).ToString());
        }

        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(new SasResponse(sasUri.ToString(), blob.Uri.ToString(), expires));
        return res;
    }
}
