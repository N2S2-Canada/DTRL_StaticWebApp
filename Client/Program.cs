using Client;
using Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// HttpClient base: use API_Prefix in Dev, same-origin in Prod (SWA will proxy /api/*)
builder.Services.AddScoped(sp =>
{
    var apiBase = builder.Configuration["ApiBaseUrl"]; // e.g., http://localhost:7071 from wwwroot/appsettings.Development.json
    if (string.IsNullOrWhiteSpace(apiBase))
        apiBase = builder.HostEnvironment.BaseAddress; // production fallback (same-origin)
    if (!apiBase.EndsWith("/")) apiBase += "/";
    return new HttpClient { BaseAddress = new Uri(apiBase, UriKind.Absolute) };
});

builder.Services.AddScoped<MetaTagService>();
builder.Services.AddScoped<CmsService>();
// Program.cs (WASM)
builder.Services.AddScoped<CmsAdminService>();


await builder.Build().RunAsync();
