using Azure;
using Azure.Data.Tables;

namespace API.Models;

public sealed class PageTextEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "PageText";
    public string RowKey { get; set; } = default!;
    public string Content { get; set; } = "";

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
