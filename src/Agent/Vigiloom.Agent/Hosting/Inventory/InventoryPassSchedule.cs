namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Schedules whole inventory passes using monotonic time.</summary>
internal sealed class InventoryPassSchedule(TimeProvider clock)
{
    private long? lastCompleted;
    private bool active;

    public bool IsDue(int intervalSeconds) => active || lastCompleted is null ||
        clock.GetElapsedTime(lastCompleted.Value) >= TimeSpan.FromSeconds(intervalSeconds);

    public void Begin() => active = true;
    public void Complete() { lastCompleted = clock.GetTimestamp(); active = false; }
    public void Reset() { lastCompleted = null; active = false; }
}
