using System.Net;
using System.Web;
using API.Data;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace API;

public sealed class GetPageText
{
    private readonly IPageTextCache _cache;
    private readonly ILogger<GetPageText> _logger;

    public GetPageText(IPageTextCache cache, ILogger<GetPageText> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// GET /api/pagetext?key=AboutUs.Lead&key=ContactUs.Email
    [Function("GetPageText")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pagetext")] HttpRequestData req)
    {
        try
        {
            var nv = HttpUtility.ParseQueryString(req.Url.Query);
            var keys = nv.GetValues("key") ?? Array.Empty<string>();

            List<object> rows;

            if (keys.Length == 0)
            {
                var dict = await _cache.GetAllAsync();
                rows = dict.Select(kv => new { Key = kv.Key, Content = kv.Value })
                           .Cast<object>().ToList();
            }
            else
            {
                var dict = await _cache.GetManyAsync(keys);
                rows = dict.Select(kv => new { Key = kv.Key, Content = kv.Value })
                           .Cast<object>().ToList();
            }

            var res = req.CreateResponse(HttpStatusCode.OK);
            res.Headers.Add("Cache-Control", "public, max-age=300"); // 5 min client cache
            await res.WriteAsJsonAsync(rows);
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch PageText.");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync("Error fetching page text.");
            return err;
        }
    }
}
