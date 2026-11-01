namespace Vigiloom.BuildingBlocks.Identifiers;

/// <summary>Creates identifiers before a resource is tracked or persisted.</summary>
public static class ResourceIds
{
    public static Guid Create() => Guid.CreateVersion7();
}
