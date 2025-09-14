using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace API;
public static class Status
{
    [Function("status")]
    public static async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "status")] HttpRequestData req)
    {
        var tenantId = Environment.GetEnvironmentVariable("TenantId");
        var clientId = Environment.GetEnvironmentVariable("ClientId");
        var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
        var connStr = Environment.GetEnvironmentVariable("SQLConnectionString");
        var scope = Environment.GetEnvironmentVariable("AzureSqlScope")
                          ?? "https://database.windows.net//.default";

        var cred = new ClientSecretCredential(tenantId, clientId, clientSecret);
        var token = cred.GetToken(new TokenRequestContext(new[] { scope }));

        await using var conn = new SqlConnection(connStr) { AccessToken = token.Token };
        await conn.OpenAsync();

        await using var cmd = new SqlCommand("SELECT TOP (1) GETUTCDATE()", conn);
        var result = await cmd.ExecuteScalarAsync();

        var resp = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await resp.WriteStringAsync($"SQL OK @ {result:O}");
        return resp;
    }
}
