using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

/// <summary>Preserves the former Agent identity history in the Operations audit feed.</summary>
internal static class AgentAuditMigration
{
    public static async Task CopyAsync(SqlConnection source, SqlConnection target, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT DeviceId, AgentId, ActorId, Action, OccurredAt, PreviousPackageId, PackageId
            FROM agent.AgentAudits ORDER BY RowId
            """, source);
        await using var rows = await command.ExecuteReaderAsync(token);
        var writer = new OperationAuditWriter();
        while (await rows.ReadAsync(token))
        {
            var device = rows.GetGuid(0);
            Guid? agent = rows.IsDBNull(1) ? null : rows.GetGuid(1);
            Guid? actor = rows.IsDBNull(2) ? null : rows.GetGuid(2);
            var action = rows.GetString(3);
            var occurredAt = rows.GetDateTimeOffset(4).ToUniversalTime();
            Guid? previous = rows.IsDBNull(5) ? null : rows.GetGuid(5);
            Guid? package = rows.IsDBNull(6) ? null : rows.GetGuid(6);
            OperationAuditDetails details = action switch
            {
                "EnrollmentIssued" => new AgentRegistrationRecorded(AgentRegistrationAction.EnrollmentIssued, agent, actor is null),
                "EnrollmentRevoked" => new AgentRegistrationRecorded(AgentRegistrationAction.EnrollmentRevoked, agent, actor is null),
                "AgentEnrolled" => new AgentRegistrationRecorded(AgentRegistrationAction.AgentEnrolled, agent, actor is null),
                "AgentRevoked" => new AgentRegistrationRecorded(AgentRegistrationAction.AgentRevoked, agent, actor is null),
                "RotationStarted" => new AgentRegistrationRecorded(AgentRegistrationAction.RotationStarted, agent, actor is null),
                "RotationFinished" => new AgentRegistrationRecorded(AgentRegistrationAction.RotationFinished, agent, actor is null),
                "VersionAssigned" or "VersionAssignmentCancelled" when agent is not null => new AgentVersionAssigned(agent.Value, previous, package),
                _ => throw new InvalidOperationException($"Unsupported legacy Agent audit action: {action}.")
            };
            await writer.AppendAsync(target, transaction, new OperationAudit(actor ?? agent ?? throw new InvalidOperationException("Agent audit has no actor."),
                device, occurredAt, details), token);
        }
    }
}
