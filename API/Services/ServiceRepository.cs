using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using API.Models;           // ServiceEntity, ServiceSectionEntity
using SharedModels;         // Service, ServiceSection
using System.Globalization;

namespace API.Services;

public interface IServiceRepository
{
    Task<List<Service>> GetAllAsync(CancellationToken ct = default);
    Task<Service?> GetByIdAsync(string id, CancellationToken ct = default);

    // Single-call upsert (service + sections)
    Task UpsertFullAsync(Service service, CancellationToken ct = default);

    // Back-compat helpers
    Task UpsertServiceAsync(Service service, CancellationToken ct = default);
    Task ReplaceSectionsAsync(string serviceId, IEnumerable<ServiceSection> sections, CancellationToken ct = default);
}

public sealed class TableServiceRepository : IServiceRepository
{
    private readonly TableClient _table;

    public TableServiceRepository(IConfiguration cfg)
    {
        var tableName = cfg["ServicesTableName"] ?? "Services";

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

    // ---------------- READ ----------------

    public async Task<List<Service>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = new List<TableEntity>();
        await foreach (var e in _table.QueryAsync<TableEntity>(x => x.PartitionKey == "Service", cancellationToken: ct))
            rows.Add(e);

        return rows
            .OrderBy(e => CoerceInt(e, "Sort", 0))
            .ThenBy(e => e.GetString("Title") ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(e => new Service
            {
                Id = e.RowKey,
                Title = e.GetString("Title") ?? "",
                Description = e.GetString("Description") ?? "",
                ImageUrl = e.GetString("ImageUrl") ?? "",
                Sort = CoerceInt(e, "Sort", 0),              // <-- include Sort in the DTO
                Sections = new()
            })
            .ToList();
    }

    public async Task<Service?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        TableEntity svcRow;
        try
        {
            var got = await _table.GetEntityAsync<TableEntity>("Service", id, cancellationToken: ct);
            svcRow = got.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var service = new Service
        {
            Id = svcRow.RowKey,
            Title = svcRow.GetString("Title") ?? "",
            Description = svcRow.GetString("Description") ?? "",
            ImageUrl = svcRow.GetString("ImageUrl") ?? "",
            Sort = CoerceInt(svcRow, "Sort", 0),            // <-- include Sort here too
            Sections = new()
        };

        var sectionPk = $"Service|{id}";
        var secs = new List<ServiceSectionEntity>();
        await foreach (var se in _table.QueryAsync<ServiceSectionEntity>(x => x.PartitionKey == sectionPk, cancellationToken: ct))
            secs.Add(se);

        service.Sections = secs
            .OrderBy(se => se.RowKey, StringComparer.Ordinal) // 000, 001, ...
            .Select(MapToSection)
            .ToList();

        return service;
    }

    // ---------------- WRITE ----------------

    public async Task UpsertFullAsync(Service service, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(service?.Id))
            throw new ArgumentException("Service.Id is required.", nameof(service));

        // Determine Sort to use (preserve existing if none provided)
        int sortToUse;
        var incomingSort = TryGetIncomingSort(service);
        TableEntity? current = await TryGetServiceRowAsync(service.Id, ct);

        if (incomingSort.HasValue)
            sortToUse = incomingSort.Value;
        else if (current is not null)
            sortToUse = CoerceInt(current, "Sort", 0);
        else
            sortToUse = await ComputeNextSortAsync(ct);

        var svc = new ServiceEntity
        {
            PartitionKey = "Service",
            RowKey = service.Id,
            Title = service.Title ?? string.Empty,
            Description = service.Description ?? string.Empty,
            ImageUrl = service.ImageUrl ?? string.Empty,
            Sort = sortToUse
        };
        await _table.UpsertEntityAsync(svc, TableUpdateMode.Replace, ct);

        await ReplaceSectionsInternalAsync(service.Id, service.Sections ?? Enumerable.Empty<ServiceSection>(), ct);
    }

    public async Task UpsertServiceAsync(Service service, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(service.Id, ct);
        service.Sections ??= existing?.Sections ?? new List<ServiceSection>();
        await UpsertFullAsync(service, ct);
    }

    public async Task ReplaceSectionsAsync(string serviceId, IEnumerable<ServiceSection> sections, CancellationToken ct = default)
        => await ReplaceSectionsInternalAsync(serviceId, sections ?? Enumerable.Empty<ServiceSection>(), ct);

    // ---------------- Internals ----------------

    private async Task ReplaceSectionsInternalAsync(string serviceId, IEnumerable<ServiceSection> sections, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
            throw new ArgumentException("serviceId is required.", nameof(serviceId));

        var partition = $"Service|{serviceId}";

        await foreach (var existing in _table.QueryAsync<TableEntity>(x => x.PartitionKey == partition, cancellationToken: ct))
            await _table.DeleteEntityAsync(existing.PartitionKey, existing.RowKey, ETag.All, ct);

        var list = sections.ToList();
        int idx = 0;

        while (idx < list.Count)
        {
            var batch = new List<TableTransactionAction>(capacity: Math.Min(100, list.Count - idx));
            for (var i = 0; i < 100 && idx < list.Count; i++, idx++)
            {
                var s = list[idx] ?? new ServiceSection();
                var e = new ServiceSectionEntity
                {
                    PartitionKey = partition,
                    RowKey = $"{idx:D3}",
                    Title = s.Title ?? string.Empty,
                    ImageUrl = s.ImageUrl ?? string.Empty,
                    BodyHtml = s.BodyHtml ?? string.Empty,
                    Sort = idx
                };
                batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, e));
            }
            await _table.SubmitTransactionAsync(batch, ct);
        }
    }

    private async Task<int> ComputeNextSortAsync(CancellationToken ct)
    {
        int max = 0;
        await foreach (var e in _table.QueryAsync<TableEntity>(x => x.PartitionKey == "Service", cancellationToken: ct))
        {
            var sort = CoerceInt(e, "Sort", 0);
            if (sort > max) max = sort;
        }
        return max + 10;
    }

    private async Task<TableEntity?> TryGetServiceRowAsync(string id, CancellationToken ct)
    {
        try
        {
            var got = await _table.GetEntityAsync<TableEntity>("Service", id, cancellationToken: ct);
            return got.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static ServiceSection MapToSection(ServiceSectionEntity e) => new()
    {
        Title = e.Title ?? "",
        ImageUrl = e.ImageUrl ?? "",
        BodyHtml = e.BodyHtml ?? ""
    };

    // ---- helpers: robust read of Sort & incoming Sort detection ----

    private static int CoerceInt(TableEntity e, string name, int def = 0)
    {
        if (TryGetAnyCase(e, name, out var val))
        {
            if (val is int i) return i;
            if (val is long l) return unchecked((int)l);
            if (val is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return def;
    }

    private static bool TryGetAnyCase(TableEntity e, string name, out object? value)
    {
        if (e.TryGetValue(name, out value)) return true; // "Sort"
        if (e.TryGetValue(char.ToLowerInvariant(name[0]) + name[1..], out value)) return true; // "sort"
        if (e.TryGetValue(char.ToUpperInvariant(name[0]) + name[1..], out value)) return true; // defensive
        value = null;
        return false;
    }

    private static int? TryGetIncomingSort(Service s)
    {
        // If your SharedModels.Service has int Sort (it looks like it does), use it; else preserve.
        var prop = s.GetType().GetProperty("Sort");
        if (prop is null || prop.PropertyType != typeof(int)) return null;
        return (int?)prop.GetValue(s);
    }
}
