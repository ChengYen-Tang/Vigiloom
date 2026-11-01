using Microsoft.AspNetCore.Identity;
using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.IdentityAccess.Domain;

public sealed class Role : IdentityRole<Guid>
{
    public Role() { Id = ResourceIds.Create(); CreatedAt = UpdatedAt = DateTimeOffset.UtcNow; }
    public Role(string name) : this() { Name = name; NormalizedName = name.ToUpperInvariant(); }
    internal Role(string name, Guid id) { Id = id; Name = name; NormalizedName = name.ToUpperInvariant(); CreatedAt = UpdatedAt = DateTimeOffset.UtcNow; }
    public long RowId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];
}
