namespace Vigiloom.Contracts.Services;

public sealed record ServiceSourceSummary(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, string Version);
public sealed record ServiceSourceRequest(string Name);
public sealed record ServiceSourceCredential(Guid SourceId, string Secret);
