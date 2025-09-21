// API/Services/CustomerContentRepository.cs
using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace API.Services;

public interface ICustomerContentRepository
{
    Task<CustomerEntry?> GetByCodeAsync(string code, CancellationToken ct = default);
    Task<List<CustomerEntry>> ListAsync(CancellationToken ct = default);
    Task<CustomerEntry> CreateCodeAsync(string? displayName, int keepAliveMonths, CancellationToken ct = default);
    Task UpsertAsync(CustomerEntry entry, CancellationToken ct = default);
    Task<bool> DeleteAsync(string code, CancellationToken ct = default);

    // NEW: used by the HTTP purge function
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// DTO used by API/functions and admin UI.
/// </summary>
public sealed class CustomerEntry
{
    public string Code { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? SharePath { get; set; }
    public int KeepAliveMonths { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
    public bool Active { get; set; }
}

public sealed class TableCustomerContentRepository : ICustomerContentRepository
{
    private const string PK = "CustomerContent";
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
                      ?? throw new InvalidOperationException("Provide StorageConnectionString or StorageAccountUrl for Table Storage.");
            _table = new TableClient(new Uri(url), tableName, new DefaultAzureCredential());
        }

        _table.CreateIfNotExists();
    }

    // -------- Entities --------

    private sealed class CustomerContentEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = PK;
        public string RowKey { get; set; } = default!; // the 5-char code
        public string? SharePath { get; set; }
        public int KeepAliveMonths { get; set; }
        public string? DisplayName { get; set; }

        // Optional custom created date; if absent, Timestamp will be used
        public DateTimeOffset? CreatedOn { get; set; }

        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }

    // -------- Helpers --------

    private static DateTimeOffset ComputeCreated(CustomerContentEntity e)
        => e.CreatedOn ?? e.Timestamp ?? DateTimeOffset.UtcNow;

    private static DateTimeOffset? ComputeExpires(CustomerContentEntity e)
        => e.KeepAliveMonths > 0 ? ComputeCreated(e).AddMonths(e.KeepAliveMonths) : null;

    private static CustomerEntry Map(CustomerContentEntity e, DateTimeOffset nowUtc)
    {
        var created = ComputeCreated(e);
        var expires = ComputeExpires(e);
        var active = expires is null || expires >= nowUtc;

        return new CustomerEntry
        {
            Code = e.RowKey,
            DisplayName = e.DisplayName,
            SharePath = e.SharePath,
            KeepAliveMonths = e.KeepAliveMonths,
            CreatedOn = created,
            ExpiresOn = expires,
            Active = active
        };
    }

    private static string NewCode()
    {
        // A-Z + 0-9 (no ambiguous chars removed here; add if you want)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // removed I, O, 0, 1 to avoid confusion
        Span<char> buf = stackalloc char[5];
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[5];
        rng.GetBytes(bytes);
        for (int i = 0; i < 5; i++) buf[i] = chars[bytes[i] % chars.Length];
        return new string(buf);
    }

    private async Task<bool> CodeExistsAsync(string code, CancellationToken ct)
    {
        try
        {
            await _table.GetEntityAsync<CustomerContentEntity>(PK, code, cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    // -------- Public API --------

    public async Task<CustomerEntry?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            var got = await _table.GetEntityAsync<CustomerContentEntity>(PK, code, cancellationToken: ct);
            return Map(got.Value, DateTimeOffset.UtcNow);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<List<CustomerEntry>> ListAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<CustomerEntry>();
        await foreach (var e in _table.QueryAsync<CustomerContentEntity>(x => x.PartitionKey == PK, cancellationToken: ct))
        {
            list.Add(Map(e, now));
        }
        // Most recent first
        return list.OrderByDescending(x => x.CreatedOn).ToList();
    }

    public async Task<CustomerEntry> CreateCodeAsync(string? displayName, int keepAliveMonths, CancellationToken ct = default)
    {
        // Generate a unique 5-char code
        string code;
        int attempts = 0;
        do
        {
            code = NewCode();
            attempts++;
        } while (await CodeExistsAsync(code, ct) && attempts < 20);

        if (attempts >= 20)
            throw new InvalidOperationException("Could not generate a unique code after many attempts.");

        var e = new CustomerContentEntity
        {
            PartitionKey = PK,
            RowKey = code,
            DisplayName = displayName,
            KeepAliveMonths = keepAliveMonths > 0 ? keepAliveMonths : 12,
            CreatedOn = DateTimeOffset.UtcNow
        };

        await _table.AddEntityAsync(e, ct);
        return Map(e, DateTimeOffset.UtcNow);
    }

    public async Task UpsertAsync(CustomerEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.Code))
            throw new ArgumentException("Code is required.", nameof(entry));

        // Try to fetch existing to preserve CreatedOn if present
        CustomerContentEntity? existing = null;
        try
        {
            var got = await _table.GetEntityAsync<CustomerContentEntity>(PK, entry.Code, cancellationToken: ct);
            existing = got.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // new
        }

        var e = new CustomerContentEntity
        {
            PartitionKey = PK,
            RowKey = entry.Code,
            DisplayName = entry.DisplayName,
            SharePath = entry.SharePath,
            KeepAliveMonths = entry.KeepAliveMonths,
            CreatedOn = existing?.CreatedOn 
    ?? (entry.CreatedOn == default ? DateTimeOffset.UtcNow : entry.CreatedOn)
        };

        await _table.UpsertEntityAsync(e, TableUpdateMode.Replace, ct);
    }

    public async Task<bool> DeleteAsync(string code, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(PK, code, ETag.All, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = new List<CustomerContentEntity>();

        await foreach (var e in _table.QueryAsync<CustomerContentEntity>(x => x.PartitionKey == PK, cancellationToken: ct))
        {
            if (e.KeepAliveMonths <= 0) continue; // never expires
            var expires = ComputeExpires(e);
            if (expires is not null && expires < now)
                expired.Add(e);
        }

        if (expired.Count == 0) return 0;

        var purged = 0;
        foreach (var batch in expired.Chunk(100))
        {
            var actions = new List<TableTransactionAction>(batch.Length);
            foreach (var e in batch)
            {
                // Use wild ETag to force delete
                var te = new TableEntity(e.PartitionKey, e.RowKey) { ETag = ETag.All };
                actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, te));
            }
            await _table.SubmitTransactionAsync(actions, ct);
            purged += actions.Count;
        }

        return purged;
    }
}
