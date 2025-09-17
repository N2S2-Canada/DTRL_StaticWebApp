using Azure;
using Azure.Data.Tables;

namespace API.Models;

public sealed class ServiceEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "Service";
    public string RowKey { get; set; } = default!; // serviceId/slug

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int Sort { get; set; } = 0; // optional for landing page ordering

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
