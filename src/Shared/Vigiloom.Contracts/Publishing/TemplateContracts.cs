namespace Vigiloom.Contracts.Publishing;

public sealed record CreatePublicationTemplate(string? Name, bool ShowLatency, bool ShowObservedAt);
public sealed record SetDefaultPublicationTemplate(string? Version, Guid? ExpectedDefaultId = null, string? ExpectedDefaultVersion = null);
public sealed record PublicationTemplateUpdate(string? Name, bool ShowLatency, bool ShowObservedAt);
public sealed record ConfirmPublicationTemplateUpdate(string? Confirmation);
public sealed record PublicationTemplateUpdatePreview(PublicationTemplateItem Current, PublicationTemplateUpdate Proposed, PublicationImpact Impact, string Confirmation);
public sealed record PublicationTemplateRetirement(Guid? ReplacementDefaultId, string? ReplacementDefaultVersion);
public sealed record PublicationTemplateRetirementPreview(PublicationTemplateItem Current, PublicationTemplateItem? ReplacementDefault, PublicationImpact Impact, string Confirmation);
public sealed record ConfirmPublicationTemplateRetirement(string? Confirmation);
public sealed record PublicationTemplateItem(Guid Id, string Name, bool ShowLatency, bool ShowObservedAt, bool IsDefault, string Version);
public sealed record PublicationTemplatePage(IReadOnlyList<PublicationTemplateItem> Items, int Page, bool HasMore);
public sealed record PublicationImpactItem(Guid Id, string Name, bool Public);
public sealed record PublicationImpact(int Count, int PublicCount, int Page, IReadOnlyList<PublicationImpactItem> Items, string Fingerprint, string TemplateVersion);
