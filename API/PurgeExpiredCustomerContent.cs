using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using API.Models;

public sealed class PurgeExpiredCustomerContent
{
    private readonly ILogger<PurgeExpiredCustomerContent> _log;
    private readonly TableClient _table;

    public PurgeExpiredCustomerContent(ILogger<PurgeExpiredCustomerContent> log, IConfiguration cfg)
    {
        _log = log;

        var tableName = cfg["CustomerContentTableName"] ?? "CustomerContent";
        var cs = cfg["StorageConnectionString"] ?? cfg["Values:StorageConnectionString"] ?? cfg["AzureWebJobsStorage"];

        _table = !string.IsNullOrWhiteSpace(cs)
            ? new TableClient(cs!, tableName)
            : new TableClient(
                new Uri(cfg["StorageAccountUrl"] ?? throw new InvalidOperationException("Missing Storage config.")),
                tableName,
                new DefaultAzureCredential());
    }

    // Runs daily at 03:00 UTC
    [Function("PurgeExpiredCustomerContent")]
#pragma warning disable IDE0060 // Remove unused parameter
    public async Task Run([TimerTrigger("0 0 3 * * *")] TimerInfo timer, FunctionContext ctx)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        var now = DateTimeOffset.UtcNow;
        var toDelete = new List<TableTransactionAction>();

        await foreach (var e in _table.QueryAsync<CustomerContentEntity>(x => x.PartitionKey == "CustomerContent"))
        {
            if (e.IsExpired(now))
            {
                toDelete.Add(new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(e.PartitionKey, e.RowKey) { ETag = ETag.All }));
                if (toDelete.Count == 100) // batch limit
                {
                    await _table.SubmitTransactionAsync(toDelete);
                    toDelete.Clear();
                }
            }
        }

        if (toDelete.Count > 0)
            await _table.SubmitTransactionAsync(toDelete);

        _log.LogInformation("CustomerContent purge completed.");
    }
}
