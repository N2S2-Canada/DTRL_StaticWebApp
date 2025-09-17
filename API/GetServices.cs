using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using API.Services;

namespace API;

public sealed class GetServices
{
    private readonly IServiceRepository _repo;

    public GetServices(IServiceRepository repo) => _repo = repo;

    // List all services for landing page cards
    [Function("GetServices")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "services")] HttpRequestData req)
    {
        var items = await _repo.GetAllAsync();
        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(items);
        return res;
    }

    // Get a single service (including Sections) for the detail page
    [Function("GetService")]
    public async Task<HttpResponseData> GetOne(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "services/{id}")] HttpRequestData req,
        string id)
    {
        var item = await _repo.GetByIdAsync(id);
        if (item is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteAsJsonAsync(item);
        return res;
    }
}
