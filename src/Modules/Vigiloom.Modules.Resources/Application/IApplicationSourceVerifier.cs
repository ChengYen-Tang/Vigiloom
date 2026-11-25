using Vigiloom.Contracts.Resources;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Checks a telemetry source with its owning observation service.</summary>
public interface IApplicationSourceVerifier
{
    Task<ApplicationSourceStatus> GetAsync(Guid sourceId, CancellationToken token);
}
