using Microsoft.AspNetCore.Identity;
using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.IdentityAccess.Domain;

public sealed class Account : IdentityUser<Guid>
{
    public Account() { Id = ResourceIds.Create(); CreatedAt = UpdatedAt = DateTimeOffset.UtcNow; }
    public long RowId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];
}
