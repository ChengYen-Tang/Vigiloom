namespace Vigiloom.Infrastructure.Operations;

public sealed class RemotePlatformAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/v1/legacy-platform-audit/retention/");
