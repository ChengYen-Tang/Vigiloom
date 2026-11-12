using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Processes;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Processes;

[SupportedOSPlatform("windows")]
public static class ServiceReader
{
    public static InventoryReadResult<ServiceObservation> Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ServiceController[] services;
        try { services = ServiceController.GetServices(); }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return new(InventoryReadStatus.Error, []); }

        var partial = services.Length > 20_000;
        var reasons = partial ? InventoryPartialReason.Truncated : 0;
        var result = new List<ServiceObservation>(Math.Min(services.Length, 20_000));
        try
        {
            foreach (var service in services.Take(20_000))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var key = service.ServiceName;
                    var name = service.DisplayName;
                    if (!Valid(key) || !Valid(name)) { partial = true; continue; }
                    string status;
                    string? mode;
                    try { status = service.Status.ToString(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    { status = "unknown"; partial = true; reasons |= Reason(ex); }
                    try { mode = service.StartType.ToString(); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                    { mode = null; partial = true; reasons |= Reason(ex); }
                    result.Add(new(key, name, status, mode));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                { partial = true; reasons |= Reason(ex); }
            }
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, result,
                partial ? reasons == 0 ? InventoryPartialReason.Other : reasons : null);
        }
        finally { foreach (var service in services) service.Dispose(); }
    }

    private static bool Valid(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 2048 && !text.Any(char.IsControl);
    private static InventoryPartialReason Reason(Exception ex) => ex is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 } ||
        ex.InnerException is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 }
        ? InventoryPartialReason.AccessDenied : InventoryPartialReason.Other;
}
