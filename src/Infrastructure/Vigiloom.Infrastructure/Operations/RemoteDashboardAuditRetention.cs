namespace Vigiloom.Infrastructure.Operations;

public sealed class RemoteDashboardAuditRetention(HttpClient client)
    : RemoteAuditRetention(client, "internal/v1/legacy-dashboard-audit/retention/");
