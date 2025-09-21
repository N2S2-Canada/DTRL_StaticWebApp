using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using API.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class GetBlobUploadSas
{
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<GetBlobUploadSas> _log;

    public GetBlobUploadSas(IConfiguration cfg, IHostEnvironment env, ILogger<GetBlobUploadSas> log)
    {
        _cfg = cfg;
        _env = env;
        _log = log;
    }

    private sealed record SasRequest(string? Container, string? FileName, string? ContentType, bool? Overwrite);
    private sealed record SasResponse(string UploadUrl, string BlobUrl, DateTimeOffset ExpiresOn);

    [Function("GetBlobUploadSas")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "blob/sas")] HttpRequestData req)
    {
        // ---- Auth: SWA admin role OR x-api-key (if configured) OR local dev (no key) ----
        if (!StaticWebAppsAuth.IsAuthorizedAdmin(req, _cfg, _env))
        {
            var status = StaticWebAppsAuth.HasPrincipal(req) ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            var deny = req.CreateResponse(status);
            await deny.WriteStringAsync("Unauthorized.");
            return deny;
        }

        var ct = req.FunctionContext.CancellationToken;

        // ---- Read JSON body (Web defaults) ----
        SasRequest? r;
        try
        {
            var serializer = new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            });
            r = await req.ReadFromJsonAsync<SasRequest>(serializer, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invalid JSON body for SAS request.");
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON.");
            return bad;
        }

        if (r is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Missing request body.");
            return bad;
        }

        var containerName = string.IsNullOrWhiteSpace(r.Container) ? (_cfg["ImagesContainer"] ?? "media") : r.Container!;
        var fileName = string.IsNullOrWhiteSpace(r.FileName) ? $"upload-{Guid.NewGuid():N}" : SanitizeFileName(r.FileName!);
        var overwrite = r.Overwrite ?? false;

        // ---- Build BlobServiceClient ----
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

        // ---- Ensure container exists (public read; change to None if you keep images private) ----
        var container = svc.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.Blob, cancellationToken: ct);

        var blob = container.GetBlobClient(fileName);

        // If overwrite is false and the blob already exists, mint a unique name to avoid collisions.
        if (!overwrite)
        {
            try
            {
                if (await blob.ExistsAsync(ct))
                {
                    var ext = Path.GetExtension(fileName);
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    fileName = $"{baseName}-{Guid.NewGuid():N}{ext}";
                    blob = container.GetBlobClient(fileName);
                }
            }
            catch (RequestFailedException ex)
            {
                _log.LogDebug(ex, "Exists check failed; proceeding.");
            }
        }

        // ---- Generate SAS ----
        var expires = DateTimeOffset.UtcNow.AddMinutes(20);
        Uri sasUri;

        // Prefer account-key SAS (CanGenerateSasUri == true when using connection string)
        if (blob.CanGenerateSasUri)
        {
            var sas = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = fileName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = expires
            };

            // Allow create/write/read; include delete if caller wants to overwrite
            sas.SetPermissions(overwrite
                ? BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read | BlobSasPermissions.Delete
                : BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read);

            sasUri = blob.GenerateSasUri(sas);
        }
        else
        {
            // User delegation SAS (AAD path) — requires proper RBAC on the storage account
            var key = await svc.GetUserDelegationKeyAsync(DateTimeOffset.UtcNow.AddMinutes(-2), expires, ct);
            var sas = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = fileName,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-2),
                ExpiresOn = expires
            };
            sas.SetPermissions(overwrite
                ? BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read | BlobSasPermissions.Delete
                : BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Read);

            var query = sas.ToSasQueryParameters(key, svc.AccountName).ToString();
            sasUri = new Uri($"{blob.Uri}?{query}");
        }

        // ---- Response ----
        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteAsJsonAsync(new SasResponse(sasUri.ToString(), blob.Uri.ToString(), expires), ct);
        return ok;
    }

    private static string SanitizeFileName(string raw)
    {
        // Basic clean-up: strip directory parts, trim, and replace spaces
        var name = Path.GetFileName(raw).Trim();
        if (string.IsNullOrWhiteSpace(name)) return $"upload-{Guid.NewGuid():N}";
        name = name.Replace(' ', '-');
        // Azure Blob names allow most chars; avoid '#' and '?' for safety
        name = name.Replace("#", "").Replace("?", "");
        return name;
    }
}
