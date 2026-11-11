namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Bounds simultaneous full snapshot comparisons within one Web process.</summary>
internal sealed class InventoryPublicationGate(int maximumConcurrency)
{
    internal static InventoryPublicationGate Shared { get; } = new(2);
    private readonly SemaphoreSlim gate = new(maximumConcurrency, maximumConcurrency);

    public bool TryEnter() => gate.Wait(0);
    public void Exit() => gate.Release();
}
