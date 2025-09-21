using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using API.Models;

namespace API.Services;

public interface ICustomerContentRepository
{
    Task<CustomerContentEntity?> GetByCodeAsync(string code, CancellationToken ct = default);
    // Optional admin helpers you may add later: Create/Upsert/Delete
}

public sealed class TableCustomerContentRepository : ICustomerContentRepository
{
    private readonly TableClient _table;

    public TableCustomerContentRepository(IConfiguration cfg)
    {
        var tableName = cfg["CustomerContentTableName"] ?? "CustomerContent";

        var cs = cfg["StorageConnectionString"] ?? cfg["Values:StorageConnectionString"] ?? cfg["AzureWebJobsStorage"];
        if (!string.IsNullOrWhiteSpace(cs))
        {
            _table = new TableClient(cs!, tableName);
        }
        else
        {
            var url = cfg["StorageAccountUrl"]
                ?? throw new InvalidOperationException("Missing StorageConnectionString or StorageAccountUrl.");
            _table = new TableClient(new Uri(url), tableName, new DefaultAzureCredential());
        }

        _table.CreateIfNotExists();
    }

    public async Task<CustomerContentEntity?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var key = code.Trim().ToUpperInvariant();

        try
        {
            var got = await _table.GetEntityAsync<CustomerContentEntity>("CustomerContent", key, cancellationToken: ct);
            var e = got.Value;

            // Expiry check based on Timestamp + KeepAliveMonths
            if (e.IsExpired(DateTimeOffset.UtcNow))
            {
                // Optional: delete expired on read
                try { await _table.DeleteEntityAsync(e.PartitionKey, e.RowKey, ETag.All, ct); } catch { /* ignore */ }
                return null;
            }

            return e;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
