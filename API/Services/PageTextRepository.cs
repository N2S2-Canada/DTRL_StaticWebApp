using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using API.Models;

namespace API.Services;

public interface IPageTextRepository
{
    Task<IDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task<IDictionary<string, string>> GetByKeysAsync(IEnumerable<string> keys, CancellationToken ct = default);
    Task UpsertAsync(IEnumerable<KeyValuePair<string, string>> rows, CancellationToken ct = default);
}

public sealed class TablePageTextRepository : IPageTextRepository
{
    private readonly TableClient _table;

    public TablePageTextRepository(IConfiguration cfg)
    {
        var tableName = cfg["CmsTableName"] ?? "Cms";

        // Dev: connection string (or Azurite)
        var cs = cfg["StorageConnectionString"] ?? cfg["Values:StorageConnectionString"] ?? cfg["AzureWebJobsStorage"];
        if (!string.IsNullOrWhiteSpace(cs))
        {
            _table = new TableClient(cs!, tableName);
        }
        else
        {
            // Prod: Managed Identity + account URL
            var url = cfg["StorageAccountUrl"];
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Missing StorageAccountUrl or StorageConnectionString.");
            _table = new TableClient(new Uri(url), tableName, new DefaultAzureCredential());
        }

        _table.CreateIfNotExists();
    }

    public async Task<IDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var e in _table.QueryAsync<PageTextEntity>(x => x.PartitionKey == "PageText", cancellationToken: ct))
            dict[e.RowKey] = e.Content ?? "";
        return dict;
    }

    public async Task<IDictionary<string, string>> GetByKeysAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var got = await _table.GetEntityAsync<PageTextEntity>("PageText", k, cancellationToken: ct);
                dict[got.Value.RowKey] = got.Value.Content ?? "";
            }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* ignore missing */ }
        }
        return dict;
    }

    public async Task UpsertAsync(IEnumerable<KeyValuePair<string, string>> rows, CancellationToken ct = default)
    {
        foreach (var r in rows)
        {
            var e = new PageTextEntity { RowKey = r.Key, Content = r.Value ?? "" };
            await _table.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);
        }
    }
}
