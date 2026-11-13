namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

/// <summary>Monotonic per-device policy generation; retained even when all scopes are retired.</summary>
public sealed class IntegrityPolicyState
{
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public long Generation { get; set; }
}
