namespace Vigiloom.Contracts.Publishing;

/// <summary>Currently authorized public device fields and history boundary.</summary>
public sealed record PublishedDevicePolicy(Guid Id, string Name, bool ShowLatency, bool ShowObservedAt,
    DateTimeOffset? PublicSince, int HistoryDays, DateTimeOffset? HistorySince, string Version, string? TemplateVersion);

/// <summary>Currently authorized public application fields and history boundary.</summary>
public sealed record PublishedServicePolicy(Guid Id, string Name, string PolicyVersion, DateTimeOffset? PublicSince, string Version);

/// <summary>Current public group identity and publication version.</summary>
public sealed record PublishedGroupPolicy(Guid Id, string Name, string Version);
