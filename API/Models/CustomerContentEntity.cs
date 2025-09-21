using Azure;
using Azure.Data.Tables;

namespace API.Models;

public sealed class CustomerContentEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "CustomerContent";
    public string RowKey { get; set; } = default!;   // 5-char code (UPPERCASE)

    public string SharePath { get; set; } = "";
    public int KeepAliveMonths { get; set; } = 0;    // 0 = never expire
    public string DisplayName { get; set; } = "";    // <-- add this

    public DateTimeOffset? Timestamp { get; set; }   // set by Tables
    public ETag ETag { get; set; }

    public bool IsExpired(DateTimeOffset nowUtc)
    {
        if (KeepAliveMonths <= 0) return false;
        var ts = Timestamp ?? nowUtc;
        return nowUtc >= ts.AddMonths(KeepAliveMonths);
    }
}
