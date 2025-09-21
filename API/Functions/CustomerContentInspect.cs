using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using API.Services;

namespace API.Functions
{
    public sealed class CustomerContentInspect
    {
        private readonly ILogger<CustomerContentInspect> _log;
        private readonly ICustomerContentRepository _repo;

        public CustomerContentInspect(ILogger<CustomerContentInspect> log, ICustomerContentRepository repo)
        {
            _log = log;
            _repo = repo;
        }

        private sealed class Output
        {
            public string? Code { get; set; }
            public bool HasStorageConn { get; set; }
            public string Result { get; set; } = "unknown";
            public object? Entity { get; set; }
            public string? Error { get; set; }
        }

        [Function("CustomerContentInspect")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customer-content/inspect")] HttpRequestData req,
            FunctionContext ctx)
        {
            var q = HttpUtility.ParseQueryString(req.Url.Query);
            var code = (q["code"] ?? "").Trim();

            var hasStorage =
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("StorageConnectionString")
                    ?? Environment.GetEnvironmentVariable("Values:StorageConnectionString")
                    ?? Environment.GetEnvironmentVariable("AzureWebJobsStorage"));

            var outp = new Output { Code = code, HasStorageConn = hasStorage };

            if (!Regex.IsMatch(code, "^[A-Za-z0-9]{5}$"))
            {
                var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                outp.Result = "bad-code";
                await bad.WriteAsJsonAsync(outp);
                return bad;
            }

            try
            {
                var row = await _repo.GetByCodeAsync(code.ToUpperInvariant(), ctx.CancellationToken);
                if (row == null)
                {
                    var nf = req.CreateResponse(HttpStatusCode.NotFound);
                    outp.Result = "not-found";
                    await nf.WriteAsJsonAsync(outp);
                    return nf;
                }

                var ok = req.CreateResponse(HttpStatusCode.OK);
                outp.Result = "ok";
                outp.Entity = new
                {
                    row.DisplayName,
                    row.SharePath,
                    row.KeepAliveMonths
                };
                await ok.WriteAsJsonAsync(outp);
                return ok;
            }
            catch (Exception ex)
            {
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                outp.Result = "exception";
                outp.Error = ex.GetType().Name + ": " + ex.Message;
                await err.WriteAsJsonAsync(outp);
                return err;
            }
        }
    }
}
