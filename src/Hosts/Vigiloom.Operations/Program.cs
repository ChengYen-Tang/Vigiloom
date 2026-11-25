using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Operations;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddModuleSqlServer<LogAdmissionDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "logquota");
builder.Services.AddScoped<PolicyAdministration>();
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
builder.Services.AddScoped<SharedLogQuota>();
var app = builder.Build();

// The deployment exposes this endpoint only to the trusted service network.
app.MapGet("/internal/v1/log-quota", async (SharedLogQuota quota, CancellationToken token) =>
    Results.Ok(Map(await quota.GetAsync(token))));
app.MapPut("/internal/v1/log-quota", async (LogQuotaUpdate input, SharedLogQuota quota, CancellationToken token) =>
{
    if (input.Settings is null || input.ActorId == Guid.Empty) return Results.BadRequest();
    var values = new LogQuotaValues(input.Settings.SourceRate, input.Settings.SourceBurst,
        input.Settings.GlobalRate, input.Settings.GlobalBurst);
    if (!values.IsValid) return Results.BadRequest();
    var updated = await quota.UpdateAsync(values, input.Settings.Version, input.ActorId, token);
    return updated is null ? Results.Conflict() : Results.Ok(Map(updated));
});
app.Run();

static QuotaContract Map(LogQuotaSnapshot value) =>
    new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);

public partial class Program;
