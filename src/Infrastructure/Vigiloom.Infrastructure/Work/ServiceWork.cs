namespace Vigiloom.Infrastructure.Work;

/// <summary>A small, stable reference to work owned by the destination service.</summary>
public sealed record ServiceWork(string Kind, string Payload, string? DeliveryId = null);

/// <summary>Executes work registered by one destination service.</summary>
public interface IServiceWorkHandler
{
    Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken);
}
