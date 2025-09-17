using Azure;
using Azure.Data.Tables;

namespace API.Models;

public sealed class ServiceSectionEntity : ITableEntity
{
    // "Service|{serviceId}"
    public string PartitionKey { get; set; } = default!;
    // zero-padded ordering key, e.g. "001", "010"
    public string RowKey { get; set; } = default!;

    public string Title { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public int Sort { get; set; } = 0; // optional (RowKey already gives order)

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
