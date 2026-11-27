namespace Vigiloom.Infrastructure.Operations;

public sealed class RemoteIdentityAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/identity/audit/retention/");
