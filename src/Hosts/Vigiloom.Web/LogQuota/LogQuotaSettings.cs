using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;

namespace Vigiloom.Web.LogQuota;

/// <summary>Connects shared quota settings to the platform's transactional operation audit.</summary>
public sealed class LogQuotaSettings(PolicyAdministration policy, IOperationAuditWriter<OperationAudit> audit)
{
    public Task<LogQuotaSnapshot> GetAsync(CancellationToken token) => policy.GetAsync(token);

    public Task<LogQuotaSnapshot?> UpdateAsync(LogQuotaValues values, string version, Guid actor, CancellationToken token) =>
        policy.UpdateAsync(values, version, actor, (connection, transaction, change, cancellationToken) =>
            audit.AppendAsync(connection, transaction, new OperationAudit(change.ActorId, change.PolicyId, change.OccurredAt,
                new LogQuotaChanged(Map(change.Previous.Values), Map(change.Current.Values),
                    change.Previous.Version, change.Current.Version)), cancellationToken), token);

    private static LogQuotaAuditValues Map(LogQuotaValues value) => new(value.SourceRate, value.SourceBurst, value.GlobalRate, value.GlobalBurst);
}
