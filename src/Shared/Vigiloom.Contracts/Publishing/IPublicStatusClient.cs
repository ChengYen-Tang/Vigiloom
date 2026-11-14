namespace Vigiloom.Contracts.Publishing;

/// <summary>Reads only anonymous publication projections in either Blazor render mode.</summary>
public interface IPublicStatusClient
{
    Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken cancellationToken);
    Task<PublicGroup?> GroupAsync(Guid id, CancellationToken cancellationToken);
    Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken);
    Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken cancellationToken);
    Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken);
    Task<PublicService?> ServiceAsync(Guid id, CancellationToken cancellationToken);
    Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
    Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
