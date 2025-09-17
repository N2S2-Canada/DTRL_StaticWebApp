using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using API.Models;
using Microsoft.Extensions.Configuration;
using SharedModels; // Service, ServiceSection

namespace API.Services;

public interface IServiceRepository
{
    Task<List<Service>> GetAllAsync(CancellationToken ct = default);
    Task<Service?> GetByIdAsync(string id, CancellationToken ct = default);

    // Optional admin helpers if you later want to save via API:
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

    public async Task<List<Service>> GetAllAsync(CancellationToken ct = default)
    {
        var items = new List<ServiceEntity>();
        await foreach (var e in _table.QueryAsync<ServiceEntity>(x => x.PartitionKey == "Service", cancellationToken: ct))
        {
            items.Add(e);
        }

        // Order by Sort then Title (stable)
        var ordered = items.OrderBy(e => e.Sort).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase);

        // Map to shared model
        return ordered.Select(e => new Service
        {
            Id = e.RowKey,
            Title = e.Title ?? "",
            Description = e.Description ?? "",
            ImageUrl = e.ImageUrl ?? "",
            Sort = e.Sort,
            Sections = new()
        }).ToList();
    }

    public async Task<Service?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        ServiceEntity svc;
        try
        {
            var got = await _table.GetEntityAsync<ServiceEntity>("Service", id, cancellationToken: ct);
            svc = got.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var partKey = $"Service|{id}";
        var sectionEntities = new List<ServiceSectionEntity>();
        await foreach (var se in _table.QueryAsync<ServiceSectionEntity>(x => x.PartitionKey == partKey, cancellationToken: ct))
        {
            sectionEntities.Add(se);
        }

        var sections = sectionEntities
            .OrderBy(se => se.RowKey, StringComparer.Ordinal) // keep zero-padded order
            .Select(se => new ServiceSection
            {
                Title = se.Title ?? "",
                ImageUrl = se.ImageUrl ?? "",
                BodyHtml = se.BodyHtml ?? ""
            })
            .ToList();

        return new Service
        {
            Id = svc.RowKey,
            Title = svc.Title ?? "",
            Description = svc.Description ?? "",
            ImageUrl = svc.ImageUrl ?? "",
            Sort = svc.Sort,
            Sections = sections
        };
    }

    // ---------- Optional admin helpers below ----------

    public async Task UpsertServiceAsync(Service service, CancellationToken ct = default)
    {
        var entity = new ServiceEntity
        {
            PartitionKey = "Service",
            RowKey = service.Id,
            Title = service.Title ?? "",
            Description = service.Description ?? "",
            ImageUrl = service.ImageUrl ?? "",
            Sort = service.Sort
        };

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    public async Task ReplaceSectionsAsync(string serviceId, IEnumerable<ServiceSection> sections, CancellationToken ct = default)
    {
        var partition = $"Service|{serviceId}";

        // 1) Delete existing sections (if you need full replace semantics)
        //    You may skip this if you prefer "upsert/merge" semantics.
        await foreach (var existing in _table.QueryAsync<TableEntity>(x => x.PartitionKey == partition, cancellationToken: ct))
        {
            await _table.DeleteEntityAsync(partition, existing.RowKey, cancellationToken: ct);
        }

        // 2) Insert new sections in a transactional batch (same partition)
        var actions = new List<TableTransactionAction>();
        int i = 0;
        foreach (var s in sections ?? Enumerable.Empty<ServiceSection>())
        {
            // zero-padded RowKey ensures stable ordering
            var rk = $"{i++:D3}";
            var e = new ServiceSectionEntity
            {
                PartitionKey = partition,
                RowKey = rk,
                Title = s.Title ?? "",
                ImageUrl = s.ImageUrl ?? "",
                BodyHtml = s.BodyHtml ?? "",
                Sort = i
            };
            actions.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, e));
            if (actions.Count == 100) // Tables limit per batch
            {
                await _table.SubmitTransactionAsync(actions, ct);
                actions.Clear();
            }
        }
        if (actions.Count > 0)
            await _table.SubmitTransactionAsync(actions, ct);
    }
}
