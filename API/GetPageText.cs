using System.Net;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using API.Services;

namespace API;

public class GetPageText
{
    private readonly IPageTextRepository _repo;

    public GetPageText(IPageTextRepository repo) => _repo = repo;

    [Function("GetPageText")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pagetext")] HttpRequestData req)
    {
        var q = HttpUtility.ParseQueryString(req.Url.Query);
        var keys = q.GetValues("key") ?? Array.Empty<string>();

        IDictionary<string, string> map =
            keys.Length == 0
                ? await _repo.GetAllAsync()
                : await _repo.GetByKeysAsync(keys);

        var list = map.Select(kv => new { key = kv.Key, content = kv.Value }).ToList();

        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(list);
        return res;
    }
}
