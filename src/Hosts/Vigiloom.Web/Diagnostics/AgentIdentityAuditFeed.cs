using Vigiloom.Contracts.Operations;
using Vigiloom.Web.Operations;

namespace Vigiloom.Web.Diagnostics;

internal sealed class AgentIdentityAuditFeed(AuditQueryReader audit)
{
    public async Task<AuditFeedPage> QueryAsync(OperationAuditQuery query, CancellationToken token)
    {
        var page = await audit.QueryAgentRegistrationAsync(query, token);
        var entries = page.Items.Select(item =>
        {
            var registration = item.AgentRegistration;
            var assignment = item.AgentAssignment;
            var action = registration?.Action switch
            {
                AgentRegistrationAction.EnrollmentIssued => AuditFeedAction.EnrollmentIssued,
                AgentRegistrationAction.EnrollmentRevoked => AuditFeedAction.EnrollmentRevoked,
                AgentRegistrationAction.AgentEnrolled => AuditFeedAction.AgentEnrolled,
                AgentRegistrationAction.AgentRevoked => AuditFeedAction.AgentRevoked,
                AgentRegistrationAction.RotationStarted => AuditFeedAction.RotationStarted,
                AgentRegistrationAction.RotationFinished => AuditFeedAction.RotationFinished,
                _ when assignment is not null => assignment.PackageId is null ? AuditFeedAction.AgentVersionAssignmentCancelled : AuditFeedAction.AgentVersionAssigned,
                _ => AuditFeedAction.Unknown
            };
            return new AuditFeedEntry(item.OccurredAt, registration?.AgentActor == true ? null : item.ActorId,
                item.ResourceId, action, AuditFeedOutcome.Recorded, AgentId: registration?.AgentId ?? assignment?.AgentId,
                AgentVersion: assignment is null ? null : new(assignment.PreviousPackageId, assignment.PackageId));
        }).ToArray();
        return new(AuditFeedSource.AgentIdentity, entries, page.Page, page.HasMore, page.LimitReached);
    }
}
