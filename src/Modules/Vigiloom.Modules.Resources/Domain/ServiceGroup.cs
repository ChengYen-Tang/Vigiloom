namespace Vigiloom.Modules.Resources.Domain;

/// <summary>A classification relationship; it does not grant access to the service.</summary>
public sealed class ServiceGroup
{
    private ServiceGroup() { }

    public ServiceGroup(Guid groupId, Guid serviceId)
    {
        if (groupId == Guid.Empty || serviceId == Guid.Empty)
            throw new ArgumentException("Resource identifiers must not be empty.");
        GroupId = groupId;
        ServiceId = serviceId;
    }

    public Guid GroupId { get; private set; }
    public Guid ServiceId { get; private set; }
}
