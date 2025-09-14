using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace Client.Services;

public sealed class CmsAdminService
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;
    private readonly IConfiguration _cfg;

    public CmsAdminService(HttpClient http, NavigationManager nav, IConfiguration cfg)
    {
        _http = http;
        _nav = nav;
        _cfg = cfg;
    }

    private Uri ApiBase() =>
        !string.IsNullOrWhiteSpace(_cfg["ApiBaseUrl"])
            ? new Uri(_cfg["ApiBaseUrl"]!, UriKind.Absolute)
            : new Uri(_nav.BaseUri);

    private void ApplyAdminHeader(HttpRequestMessage req)
    {
        var apiKey = _cfg["CmsAdminApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Add("x-api-key", apiKey);
    }

    // Use mutable properties for Blazor @bind
    public sealed class Row
    {
        public string Key { get; set; } = "";
        public string Content { get; set; } = "";
    }

    public async Task<List<Row>> GetAllAsync(CancellationToken ct = default)
    {
        var uri = new Uri(ApiBase(), "api/pagetext"); // no keys -> returns all
        return await _http.GetFromJsonAsync<List<Row>>(uri.ToString(), ct) ?? new();
    }

    public async Task SaveAsync(IEnumerable<Row> rows, CancellationToken ct = default)
    {
        var uri = new Uri(ApiBase(), "api/pagetext");
        using var req = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyAdminHeader(req);
        req.Content = JsonContent.Create(rows);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
