using Vigiloom.Contracts.Operations;

namespace Vigiloom.Contracts.Resources;

/// <summary>A publication-template change delivered to the audit service.</summary>
public sealed record PublicTemplateAuditRecorded(Guid ActorId, Guid TemplateId, DateTimeOffset OccurredAt,
    PublicationTemplateAction Action, int AffectedDevices);
