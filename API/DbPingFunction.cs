using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public class DbPingFunction
{
    [Function("dbping")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dbping")] HttpRequestData req)
    {
        var tenantId = Environment.GetEnvironmentVariable("TenantId");
        var clientId = Environment.GetEnvironmentVariable("ClientId");
        var secret = Environment.GetEnvironmentVariable("ClientSecret");
        var connStr = Environment.GetEnvironmentVariable("SQLConnectionString");
        var scope = Environment.GetEnvironmentVariable("AzureSqlScope") ?? "https://database.windows.net//.default";

        if (new[] { tenantId, clientId, secret, connStr }.Any(string.IsNullOrWhiteSpace))
        {
            var bad = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await bad.WriteAsJsonAsync(new { ok = false, message = "Missing required configuration." });
            return bad;
        }

        try
        {
            var cred = new Azure.Identity.ClientSecretCredential(tenantId, clientId, secret);
            var token = cred.GetToken(new Azure.Core.TokenRequestContext(new[] { scope }));

            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr) { AccessToken = token.Token };
            await conn.OpenAsync();

            const string sql = "SELECT DB_NAME() AS db, SUSER_SNAME() AS asUser, CONVERT(varchar(23), SYSUTCDATETIME(), 126) AS utc";
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            r.Read();

            var ok = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await ok.WriteAsJsonAsync(new
            {
                ok = true,
                db = r["db"]?.ToString(),
                asUser = r["asUser"]?.ToString(),
                utc = r["utc"]?.ToString()
            });
            return ok;
        }
        catch (Exception ex)
        {
            var err = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await err.WriteAsJsonAsync(new { ok = false, message = ex.Message });
            return err;
        }
    }
}
