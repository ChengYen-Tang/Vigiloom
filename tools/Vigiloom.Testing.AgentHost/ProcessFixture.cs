using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

public static class ProcessFixture
{
    public static Task<InventoryReadResult<InventoryEntry>> ReadAsync(CancellationToken token) =>
        new InventoryCollector(TimeProvider.System).ReadAsync(InventorySource.HostProcesses, token, collectCommandLine: true);

    public static Task<InventoryReadResult<InventoryEntry>> ReadListenersAsync(CancellationToken token) =>
        new InventoryCollector(TimeProvider.System).ReadAsync(InventorySource.HostListeners, token);

    public static Task<InventoryReadResult<InventoryEntry>> ReadServicesAsync(CancellationToken token) =>
        new InventoryCollector(TimeProvider.System).ReadAsync(InventorySource.HostServices, token);
}
