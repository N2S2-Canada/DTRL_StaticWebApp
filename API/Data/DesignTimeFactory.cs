using API.Data;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var cwd = Directory.GetCurrentDirectory();

        // Adjust path if your Functions project isn't ../API
        var apiLocalSettings = Path.Combine(cwd, "..", "API", "local.settings.json");

        var config = new ConfigurationBuilder()
            .SetBasePath(cwd)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{env}.json", optional: true)
            .AddJsonFile("local.settings.json", optional: true)   // current project
            .AddJsonFile(apiLocalSettings, optional: true)        // Functions project
            .AddUserSecrets<AppDbContextFactory>(optional: true)  // <-- pulls SP creds here
            .AddEnvironmentVariables()
            .Build();

        // 1) Build the SP credential from User Secrets
        var tenantId = config["AzureAd:TenantId"];
        var clientId = config["AzureAd:ClientId"];
        var clientSecret = config["AzureAd:ClientSecret"];

        if (string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "Missing AzureAd:TenantId/ClientId/ClientSecret in User Secrets for the service principal.");
        }

        TokenCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

        // 2) Find a credential-less connection string
        var connString =
            config.GetConnectionString("DefaultConnection")
            ?? config["Values:SQLConnectionString"]
            ?? config["SQLConnectionString"];

        if (string.IsNullOrWhiteSpace(connString))
            throw new InvalidOperationException(
                "No connection string found. Set ConnectionStrings:DefaultConnection (recommended) " +
                "or Values:SQLConnectionString in local.settings.json / user secrets.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connString, sql =>
            {
                sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
                sql.CommandTimeout(180);
            })
            .AddInterceptors(new AadAccessTokenInterceptor(credential)) // inject SP token per open
            .Options;

        return new AppDbContext(options);
    }
}

/// Injects an Entra access token (for the SP) into every SqlConnection open.
file sealed class AadAccessTokenInterceptor : DbConnectionInterceptor
{
    private static readonly TokenRequestContext Scope =
        new(new[] { "https://database.windows.net/.default" });

    private readonly TokenCredential _credential;

    public AadAccessTokenInterceptor(TokenCredential credential) => _credential = credential;

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        AttachToken(connection);
        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        await AttachTokenAsync(connection, cancellationToken);
        return result;
    }

    private void AttachToken(DbConnection connection)
    {
        if (connection is SqlConnection sql)
        {
            var token = _credential.GetToken(Scope, default);
            sql.AccessToken = token.Token;
        }
    }

    private async Task AttachTokenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection is SqlConnection sql)
        {
            var token = await _credential.GetTokenAsync(Scope, ct);
            sql.AccessToken = token.Token;
        }
    }
}
