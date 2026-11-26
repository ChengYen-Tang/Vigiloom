using System.Diagnostics.Metrics;
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
    private const string MeterName = "Vigiloom.Self";
    private static readonly Meter Meter = new(MeterName);

    static SelfTelemetryRegistration() => Meter.CreateObservableGauge("vigiloom.service.running", () => 1,
        description: "The Vigiloom process is running; this does not indicate service health.");

    public static void AddVigiloomSelfTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        if (!builder.Configuration.GetValue("Vigiloom:SelfTelemetry:Enabled", true)) return;
        var address = builder.Configuration["Vigiloom:SelfTelemetry:Url"];
        if (string.IsNullOrWhiteSpace(address)) address = builder.Configuration["Services:Observability:Url"];
        if (string.IsNullOrWhiteSpace(address)) address = builder.Environment.IsDevelopment() ? "http://localhost:5057" : "http://vigiloom-observability";
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
            .WithMetrics(metrics => metrics.AddMeter(MeterName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithLogging(_ => { }, options => { options.IncludeFormattedMessage = true; options.IncludeScopes = true; })
            .UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, endpoint);
    }
}
