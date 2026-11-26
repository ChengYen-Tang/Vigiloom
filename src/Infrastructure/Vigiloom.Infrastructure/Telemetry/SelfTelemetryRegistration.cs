using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Vigiloom.Infrastructure.Telemetry;

/// <summary>Exports this service's telemetry to Vigiloom's trusted internal receiver.</summary>
public static class SelfTelemetryRegistration
{
    public static void AddVigiloomSelfTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var address = builder.Configuration["Vigiloom:SelfTelemetry:Url"];
        if (string.IsNullOrWhiteSpace(address) || !builder.Configuration.GetValue("Vigiloom:SelfTelemetry:Enabled", true)) return;
        var endpoint = new Uri(address.TrimEnd('/') + "/internal/v1/self-otlp/");
        var instance = $"{Environment.MachineName}-{Environment.ProcessId}";
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("Microsoft", LogLevel.None);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("System", LogLevel.None);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>("OpenTelemetry", LogLevel.None);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceInstanceId: instance)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/internal/v1/self-otlp"))
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request =>
                    request.RequestUri?.AbsolutePath.StartsWith("/internal/v1/self-otlp", StringComparison.OrdinalIgnoreCase) != true))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithLogging(_ => { }, options => { options.IncludeFormattedMessage = true; options.IncludeScopes = true; })
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, endpoint);
    }
}
