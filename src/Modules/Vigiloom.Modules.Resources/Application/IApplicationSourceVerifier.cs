namespace Vigiloom.Modules.Resources.Application;

/// <summary>Checks a telemetry source with its owning observation service.</summary>
public interface IApplicationSourceVerifier
{
    Task<bool> IsActiveAsync(Guid sourceId, CancellationToken token);
}
