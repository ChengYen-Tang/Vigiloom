using System.Net;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal enum ReportedHealth { Healthy, Degraded, Unhealthy }

/// <summary>Interprets the default ASP.NET Core health-check response.</summary>
internal static class HealthResponse
{
    internal static ReportedHealth? Parse(HttpStatusCode status, ReadOnlySpan<char> body)
    {
        body = body.Trim();
        if (status == HttpStatusCode.ServiceUnavailable && body.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase))
            return ReportedHealth.Unhealthy;
        if ((int)status is < 200 or > 299) return null;
        if (body.Equals("Healthy", StringComparison.OrdinalIgnoreCase)) return ReportedHealth.Healthy;
        if (body.Equals("Degraded", StringComparison.OrdinalIgnoreCase)) return ReportedHealth.Degraded;
        return body.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) ? ReportedHealth.Unhealthy : null;
    }
}
