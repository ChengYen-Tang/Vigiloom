namespace Vigiloom.Contracts.Publishing;

public sealed record CreatePublicationTemplate(string? Name, bool ShowLatency, bool ShowObservedAt);
public sealed record PublicationTemplateItem(Guid Id, string Name, bool ShowLatency, bool ShowObservedAt, bool IsDefault, string Version);
public sealed record PublicationTemplatePage(IReadOnlyList<PublicationTemplateItem> Items, int Page, bool HasMore);
public sealed record PublicationImpactItem(Guid Id, string Name, bool Public);
public sealed record PublicationImpact(int Count, int PublicCount, int Page, IReadOnlyList<PublicationImpactItem> Items, string Fingerprint, string TemplateVersion);
