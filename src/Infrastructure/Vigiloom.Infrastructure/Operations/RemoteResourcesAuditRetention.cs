namespace Vigiloom.Infrastructure.Operations;

public sealed class RemoteResourcesAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/v1/audit/retention/");
