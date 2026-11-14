using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Publishing;

/// <summary>Uses anonymous readers with bounded server-circuit status and history requests.</summary>
internal sealed class ServerPublicStatusClient(PublicReader reader, PublicDeviceHistoryReader devices,
    PublicServiceHistoryReader services, PublicHistoryAdmission admission, PublicStatusAdmission statusAdmission) : IPublicStatusClient
{
    public Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken cancellationToken) =>
        ReadStatusAsync(ct => reader.GroupsAsync(Validate(search, page), page, ct), cancellationToken);
    public Task<PublicGroup?> GroupAsync(Guid id, CancellationToken cancellationToken) => ReadStatusAsync(ct => reader.GroupAsync(id, ct), cancellationToken);
    public Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) =>
        ReadStatusAsync(ct => reader.DevicesAsync(Validate(search, page), page, groupId, ct), cancellationToken);
    public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken cancellationToken) => ReadStatusAsync(ct => reader.DeviceAsync(id, ct), cancellationToken);
    public Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) =>
        ReadStatusAsync(ct => reader.ServicesAsync(Validate(search, page), page, groupId, ct), cancellationToken);
    public Task<PublicService?> ServiceAsync(Guid id, CancellationToken cancellationToken) => ReadStatusAsync(ct => reader.ServiceAsync(id, ct), cancellationToken);
    public Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        PublicDeviceHistoryReader.Valid(from, to) ? ReadHistoryAsync(ct => devices.ReadAsync(id, from, to, ct), cancellationToken) : throw new ArgumentException("Invalid public history range.");
    public Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        PublicServiceHistoryReader.Valid(from, to) ? ReadHistoryAsync(ct => services.ReadAsync(id, from, to, ct), cancellationToken) : throw new ArgumentException("Invalid public history range.");
    public Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        PublicServiceHistoryReader.ValidPeriods(from, to) ? ReadHistoryAsync(ct => services.ReadPeriodsAsync(id, from, to, ct), cancellationToken) : throw new ArgumentException("Invalid public history range.");

    private async Task<T?> ReadHistoryAsync<T>(Func<CancellationToken, Task<T?>> read, CancellationToken token) where T : class
    {
        if (!await admission.Gate.WaitAsync(0, token)) throw new InvalidOperationException("Public history is busy.");
        using var timeout = new CancellationTokenSource(admission.Timeout, admission.Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Public history timed out."); }
        finally { admission.Gate.Release(); }
    }

    private async Task<T> ReadStatusAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        if (!await statusAdmission.Gate.WaitAsync(0, token)) throw new InvalidOperationException("Public status is busy.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await read(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Public status timed out."); }
        finally { statusAdmission.Gate.Release(); }
    }

    private static string? Validate(string? search, int page)
    {
        if (page is < 1 or > PublicReader.MaximumPage || search?.Length > 256) throw new ArgumentException("Invalid public page or search.");
        return search?.Trim();
    }
}
