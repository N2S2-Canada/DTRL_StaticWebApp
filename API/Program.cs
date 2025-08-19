using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .ConfigureLogging(logging =>
    {
        // Optional: clear default providers so you only get what you configure
        logging.ClearProviders();

        // Add console logging (will show in VS Output window and Azure Log Stream)
        logging.AddConsole();

        // Set a minimum log level (Information is a good default)
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

host.Run();
