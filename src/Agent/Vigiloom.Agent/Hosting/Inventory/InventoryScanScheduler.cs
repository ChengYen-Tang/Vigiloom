using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Alternates due inventory and process passes while keeping one durable producer.</summary>
internal sealed class InventoryScanScheduler(TimeProvider clock, InventorySource[] inventorySources, InventorySource[] processSources)
{
    private readonly InventoryPassSchedule inventory = new(clock);
    private readonly InventoryPassSchedule processes = new(clock);
    private bool? activeProcessPass;
    private bool lastWasProcess = true;
    private int index;

    public InventorySource? Next(int inventoryIntervalSeconds, int processIntervalSeconds)
    {
        if (activeProcessPass is null)
        {
            var inventoryDue = inventorySources.Length > 0 && inventory.IsDue(inventoryIntervalSeconds);
            var processDue = processSources.Length > 0 && processes.IsDue(processIntervalSeconds);
            if (!inventoryDue && !processDue) return null;
            activeProcessPass = processDue && (!inventoryDue || !lastWasProcess);
            index = 0;
            (activeProcessPass.Value ? processes : inventory).Begin();
        }
        var sources = activeProcessPass.Value ? processSources : inventorySources;
        return sources[index];
    }

    public void FinishSource(InventorySource source)
    {
        if (activeProcessPass is null) return;
        var sources = activeProcessPass.Value ? processSources : inventorySources;
        var finished = Array.IndexOf(sources, source);
        // A pending generation from an earlier process can be reconciled first.
        if (finished < 0) return;
        index = finished + 1;
        if (index < sources.Length) return;
        (activeProcessPass.Value ? processes : inventory).Complete();
        lastWasProcess = activeProcessPass.Value;
        activeProcessPass = null;
        index = 0;
    }

    public void Reset()
    {
        inventory.Reset();
        processes.Reset();
        activeProcessPass = null;
        lastWasProcess = true;
        index = 0;
    }
}
