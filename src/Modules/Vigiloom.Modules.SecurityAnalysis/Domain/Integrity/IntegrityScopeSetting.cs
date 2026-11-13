using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

/// <summary>An independently versioned scope; retired IDs are retained and never reused.</summary>
public sealed class IntegrityScopeSetting
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public long Revision { get; set; }
    public string DefinitionJson { get; set; } = "";
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
    public byte[] Version { get; private set; } = [];
}
