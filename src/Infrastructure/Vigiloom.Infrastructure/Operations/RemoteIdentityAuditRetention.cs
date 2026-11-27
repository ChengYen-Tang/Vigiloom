namespace Vigiloom.Infrastructure.Operations;

public sealed record IdentityAuditRetentionRequest(DateTimeOffset Cutoff, int BatchSize = 0);

public sealed class RemoteIdentityAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/identity/audit/retention/");
