namespace Vigiloom.Contracts.Publishing;

/// <summary>Internal policy and target needed to build public device status; never return directly to anonymous clients.</summary>
public sealed record PublishedDevicePolicy(Guid Id, string Name, string Target, bool Enabled, bool ShowLatency, bool ShowObservedAt,
    DateTimeOffset? PublicSince, int HistoryDays, DateTimeOffset? HistorySince, string Version, string? TemplateVersion);

/// <summary>Currently authorized public application fields and history boundary.</summary>
public sealed record PublishedServicePolicy(Guid Id, string Name, string PolicyVersion, DateTimeOffset? PublicSince, string Version);

/// <summary>Current public group identity and publication version.</summary>
public sealed record PublishedGroupPolicy(Guid Id, string Name, string Version);

/// <summary>Internal public group snapshot with counts of its published members.</summary>
public sealed record PublishedGroupSummary(Guid Id, string Name, int DeviceCount, int ServiceCount, string Version);
