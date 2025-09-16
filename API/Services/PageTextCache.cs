using Microsoft.Extensions.Caching.Memory;
using API.Services; // IPageTextRepository

public interface IPageTextCache
{
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
    Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys, CancellationToken ct = default);
    void Seed(IDictionary<string, string> initial);
    void Invalidate();
}

public sealed class PageTextCache : IPageTextCache
{
    private readonly IPageTextRepository _repo;
    private readonly IMemoryCache _mem;
    private static readonly string CacheKey = "PageText:All";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public PageTextCache(IPageTextRepository repo, IMemoryCache mem)
    {
        _repo = repo;
        _mem = mem;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        if (_mem.TryGetValue(CacheKey, out Dictionary<string, string>? dict) && dict is not null)
            return dict;

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock
            if (_mem.TryGetValue(CacheKey, out dict) && dict is not null)
                return dict;

            var data = await _repo.GetAllAsync(ct);
            dict = new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);

            _mem.Set(CacheKey, dict, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Ttl,
                Size = dict.Count
            });

            return dict;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Dictionary<string, string>> GetManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        var set = new HashSet<string>(keys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return all.Where(kv => set.Contains(kv.Key))
                  .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    public void Seed(IDictionary<string, string> initial)
    {
        var dict = new Dictionary<string, string>(initial ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _mem.Set(CacheKey, dict, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Ttl,
            Size = dict.Count
        });
    }

    public void Invalidate() => _mem.Remove(CacheKey);
}
