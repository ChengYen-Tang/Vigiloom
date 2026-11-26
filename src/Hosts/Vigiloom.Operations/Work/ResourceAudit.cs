using System.Text.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Operations.Work;

/// <summary>Maps existing resource-service audit work to approved operation audit records.</summary>
internal static class ResourceAudit
{
    internal static OperationAudit Convert(ServiceWork work) => work.Kind switch
    {
        nameof(GroupAuditRecorded) => Group(Read<GroupAuditRecorded>(work)),
        nameof(PublicTemplateAuditRecorded) => Template(Read<PublicTemplateAuditRecorded>(work)),
        nameof(PublicationSettingAudit) => Publication(Read<PublicationSettingAudit>(work)),
        nameof(ApplicationApprovalAudit) => Approval(Read<ApplicationApprovalAudit>(work)),
        nameof(ApplicationStateAudit) => ApplicationState(Read<ApplicationStateAudit>(work)),
        nameof(AgentRegistrationAudit) => Registration(Read<AgentRegistrationAudit>(work)),
        nameof(AgentVersionAssignmentAudit) => Assignment(Read<AgentVersionAssignmentAudit>(work)),
        _ => throw new InvalidOperationException("Unsupported operations work type.")
    };

    private static T Read<T>(ServiceWork work) where T : class => JsonSerializer.Deserialize<T>(work.Payload)
        ?? throw new InvalidOperationException("Resource audit work is empty.");

    private static OperationAudit Group(GroupAuditRecorded value) => new(value.ActorId, value.GroupId, value.OccurredAt,
        new GroupChanged(value.Action, value.Added, value.Removed));

    private static OperationAudit Template(PublicTemplateAuditRecorded value) => new(value.ActorId, value.TemplateId, value.OccurredAt,
        new PublicationTemplateChanged(value.Action, value.AffectedDevices));

    private static OperationAudit Publication(PublicationSettingAudit value) => new(value.ActorId, value.ResourceId, value.OccurredAt,
        new PublicationChanged(value.Kind, value.PreviousPublic, value.CurrentPublic, value.NameChanged, value.TemplateChanged, value.PolicyChanged));

    private static OperationAudit Approval(ApplicationApprovalAudit value) => new(value.ActorId, value.ServiceId, value.OccurredAt,
        new ServiceApproved(value.SourceId));

    private static OperationAudit ApplicationState(ApplicationStateAudit value) => new(value.ActorId, value.ServiceId, value.OccurredAt,
        new ServiceCollectionChanged(value.Action switch
        {
            "stop" => ServiceCollectionAction.Stopped,
            "resume" => ServiceCollectionAction.Resumed,
            "archive" => ServiceCollectionAction.Archived,
            "restore" => ServiceCollectionAction.Restored,
            _ => throw new ArgumentException("Unsupported application action.")
        }));

    private static OperationAudit Registration(AgentRegistrationAudit value)
    {
        var action = value.Action switch
        {
            "EnrollmentIssued" => AgentRegistrationAction.EnrollmentIssued,
            "AgentEnrolled" => AgentRegistrationAction.AgentEnrolled,
            "RotationStarted" => AgentRegistrationAction.RotationStarted,
            "RotationFinished" => AgentRegistrationAction.RotationFinished,
            "EnrollmentRevoked" => AgentRegistrationAction.EnrollmentRevoked,
            "AgentRevoked" => AgentRegistrationAction.AgentRevoked,
            _ => throw new ArgumentException("Unsupported Agent registration action.")
        };
        var actor = value.ActorId ?? value.AgentId ?? throw new ArgumentException("An actor or Agent is required.");
        return new(actor, value.DeviceId, value.OccurredAt, new AgentRegistrationRecorded(action, value.AgentId, value.ActorId is null));
    }

    private static OperationAudit Assignment(AgentVersionAssignmentAudit value) => new(value.ActorId, value.DeviceId, value.OccurredAt,
        new AgentVersionAssigned(value.AgentId, value.PreviousPackageId, value.PackageId));
}
