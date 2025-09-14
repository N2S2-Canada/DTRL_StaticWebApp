using API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

public interface IPageTextCache
{
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys, CancellationToken ct = default);
    void Invalidate();
}

public sealed class PageTextCache : IPageTextCache
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IMemoryCache _mem;
    private static readonly string CacheKey = "PageText:All";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    public PageTextCache(IDbContextFactory<AppDbContext> factory, IMemoryCache mem)
    {
        _factory = factory;
        _mem = mem;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        if (_mem.TryGetValue(CacheKey, out Dictionary<string, string>? dict) && dict is not null)
            return dict;

        using var db = await _factory.CreateDbContextAsync(ct);
        var list = await db.PageTexts.AsNoTracking().ToListAsync(ct);
        dict = list.ToDictionary(p => p.Key, p => p.Content, StringComparer.OrdinalIgnoreCase);

        _mem.Set(CacheKey, dict, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Ttl,
            Size = dict.Count
        });

        return dict;
    }

    public async Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        var set = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
        return all.Where(kv => set.Contains(kv.Key))
                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public void Invalidate() => _mem.Remove(CacheKey);
}
