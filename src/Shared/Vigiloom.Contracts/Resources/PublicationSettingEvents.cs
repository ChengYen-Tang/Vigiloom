using Vigiloom.Contracts.Operations;

namespace Vigiloom.Contracts.Resources;

public sealed record PublicationSettingAudit(Guid ActorId, Guid ResourceId, DateTimeOffset OccurredAt,
    PublicationResourceKind Kind, bool PreviousPublic, bool CurrentPublic, bool NameChanged, bool TemplateChanged, bool PolicyChanged);

/// <summary>Notifies application monitoring that publication policy changed.</summary>
public sealed record ApplicationPublicationChanged(Guid ServiceId, byte[] Version);
