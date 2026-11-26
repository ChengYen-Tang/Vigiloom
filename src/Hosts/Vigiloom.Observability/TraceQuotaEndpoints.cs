using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using LogQuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

namespace Vigiloom.Observability;

internal static class TraceQuotaEndpoints
{
    internal static void MapTraceQuota(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/trace-quota");
        group.MapGet("/", async (TracePolicyAdministration policy, CancellationToken token) => Results.Ok(Map(await policy.GetAsync(token))));
        group.MapPut("/", async (UpdateTraceQuotaCommand command, TracePolicyAdministration policy,
            IOperationAuditWriter<OperationAudit> audit, CancellationToken token) =>
        {
            var input = command.Input;
            var values = new LogQuotaValues(input.SourceRate, input.SourceBurst, input.GlobalRate, input.GlobalBurst);
            if (!values.IsValid || command.ActorId == Guid.Empty) return Results.BadRequest();
            var result = await policy.UpdateAsync(values, input.Version, command.ActorId, (connection, transaction, change, cancellation) =>
                audit.AppendAsync(connection, transaction, new OperationAudit(change.ActorId, change.PolicyId, change.OccurredAt,
                    new TraceQuotaChanged(AuditValues(change.Previous.Values), AuditValues(change.Current.Values), change.Previous.Version, change.Current.Version)), cancellation), token);
            return result is null ? Results.Conflict() : Results.Ok(Map(result));
        });
    }
    private static LogQuotaContract Map(LogQuotaSnapshot value) => new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);
    private static LogQuotaAuditValues AuditValues(LogQuotaValues value) => new(value.SourceRate, value.SourceBurst, value.GlobalRate, value.GlobalBurst);
}
