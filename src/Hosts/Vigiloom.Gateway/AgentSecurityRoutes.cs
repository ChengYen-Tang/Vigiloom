using Yarp.ReverseProxy.Configuration;

namespace Vigiloom.Gateway;

internal sealed class AgentSecurityRoutes(IConfiguration configuration) : IProxyConfigFilter
{
    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel) => ValueTask.FromResult(cluster);
    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(configuration.GetValue("Vigiloom:Gateway:SecurityCutover", false) &&
            (route.RouteId == "agent-batches" || route.RouteId.StartsWith("agent-security-", StringComparison.Ordinal))
            ? route with { ClusterId = "host-monitoring" } : route);
}
