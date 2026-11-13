using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var instanceId = builder.Configuration["VIGILOOM_SAMPLE_INSTANCE_ID"];
if (string.IsNullOrWhiteSpace(instanceId))
    throw new InvalidOperationException("Set VIGILOOM_SAMPLE_INSTANCE_ID to a stable, unique value for this running instance.");
var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "vigiloom-sample";
var environment = builder.Configuration["VIGILOOM_SAMPLE_ENVIRONMENT"] ?? "development";
var serviceNamespace = builder.Configuration["VIGILOOM_SAMPLE_NAMESPACE"] ?? "";

builder.Services.AddHealthChecks();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName, serviceNamespace: serviceNamespace, serviceInstanceId: instanceId)
        .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environment)]))
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation(options => options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation();
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_TRACES_SAMPLER"]))
            tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1)));
    })
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithLogging(_ => { }, options => { options.IncludeFormattedMessage = true; options.IncludeScopes = true; })
    .UseOtlpExporter();

var app = builder.Build();
app.MapGet("/", (ILogger<Program> logger) =>
{
    logger.LogInformation("The sample received a request");
    return Results.Ok(new { message = "Vigiloom sample is running" });
});
app.MapHealthChecks("/health");
await app.RunAsync();

public partial class Program;
