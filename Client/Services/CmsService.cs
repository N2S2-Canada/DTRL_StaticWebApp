using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;

public sealed class CmsService
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private readonly string? _apiBaseOverride;

    // Positive cache: Key -> Content
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Negative cache: keys known missing until <expires>
    private readonly Dictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

    public CmsService(HttpClient http, NavigationManager nav, IConfiguration config)
    {
        _http = http;
        _nav = nav;
        _apiBaseOverride = config["ApiBaseUrl"]; // present in Development only
    }

    private Uri ApiBase() =>
        !string.IsNullOrWhiteSpace(_apiBaseOverride)
            ? new Uri(_apiBaseOverride!, UriKind.Absolute)
            : new Uri(_nav.BaseUri); // production/SWA: same-origin

    private Uri BuildUriForKeys(IEnumerable<string> keys)
    {
        var query = string.Join("&", keys.Select(k => $"key={Uri.EscapeDataString(k)}"));
        var path = string.IsNullOrEmpty(query) ? "api/pagetext" : $"api/pagetext?{query}";
        return new Uri(ApiBase(), path);
    }

    /// <summary>
    /// Batch-load and cache a set of keys. Skips keys already cached
    /// and keys recently negative-cached to avoid extra round trips.
    /// </summary>
    public async Task WarmAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = keys
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !_cache.ContainsKey(k) && (!_missing.TryGetValue(k, out var until) || now >= until))
            .ToArray();

        if (wanted.Length == 0) return;

        var uri = BuildUriForKeys(wanted);
        var rows = await _http.GetFromJsonAsync<List<PageTextDto>>(uri.ToString(), ct) ?? new();

        // Cache found rows
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            _cache[r.Key] = r.Content;
            found.Add(r.Key);
        }

        // Negative-cache the ones not returned
        var expires = now.Add(NegativeCacheTtl);
        foreach (var k in wanted)
            if (!found.Contains(k))
                _missing[k] = expires;
    }

    /// <summary>
    /// Get a single key. Uses positive cache, then negative cache, then fetches.
    /// </summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var now = DateTimeOffset.UtcNow;
        if (_missing.TryGetValue(key, out var until) && now < until)
            return null; // still considered missing; show fallback

        // Fetch just this key
        var uri = BuildUriForKeys(new[] { key });
        var rows = await _http.GetFromJsonAsync<List<PageTextDto>>(uri.ToString(), ct) ?? new();
        var content = rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Content;

        if (!string.IsNullOrWhiteSpace(content))
        {
            _cache[key] = content;
            _missing.Remove(key);
        }
        else
        {
            _missing[key] = now.Add(NegativeCacheTtl);
        }

        return content;
    }

    private sealed class PageTextDto
    {
        public string Key { get; set; } = default!;
        public string Content { get; set; } = default!;
    }
    public bool TryGetCached(string key, out string? value)
    => _cache.TryGetValue(key, out value);
}
