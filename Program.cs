using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using JsonToSentinelFunction;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    })
    .ConfigureLogging(logging =>
    {
        // logging.SetMinimumLevel(LogLevel.Information);
        logging.Services.Configure<LoggerFilterOptions>(options =>
       {
           LoggerFilterRule defaultRule = options.Rules.FirstOrDefault(rule => rule.ProviderName
               == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
           if (defaultRule is not null)
           {
               options.Rules.Remove(defaultRule);
           }
       });
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<JsonProcessor>>();
JsonProcessor.ValidateMonitorAuthenticationConfig(logger);

host.Run();