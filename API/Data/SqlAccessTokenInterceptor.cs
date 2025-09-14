using System.Data.Common;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace API.Data;

public sealed class SqlAccessTokenInterceptor : DbConnectionInterceptor
{
    private readonly ClientSecretCredential _cred;
    private static readonly TokenRequestContext _ctx =
        new(new[] { "https://database.windows.net//.default" });

    public SqlAccessTokenInterceptor(IConfiguration cfg)
    {
        _cred = new ClientSecretCredential(cfg["TenantId"]!, cfg["ClientId"]!, cfg["ClientSecret"]!);
    }

    public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqlConnection sql)
        {
            var token = await _cred.GetTokenAsync(_ctx, cancellationToken);
            sql.AccessToken = token.Token;
        }
        await base.ConnectionOpeningAsync(connection, eventData, result, cancellationToken);
        return result;
    }
}
