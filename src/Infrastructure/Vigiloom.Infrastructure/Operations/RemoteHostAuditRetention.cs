namespace Vigiloom.Infrastructure.Operations;

public sealed class RemoteHostAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/v1/audit/retention/");
