namespace Vigiloom.Contracts.Operations;

/// <summary>Updates the LINE channel on behalf of an administrator.</summary>
public sealed record LineChannelChange(Guid ActorId, [property: System.Text.Json.Serialization.JsonRequired] bool Enabled, string? AccessToken, string? ChannelSecret, int? AggregationSeconds);

/// <summary>Updates a discovered LINE group on behalf of an administrator.</summary>
public sealed record LineGroupChange(Guid ActorId, string? Name, [property: System.Text.Json.Serialization.JsonRequired] bool Approved);

/// <summary>Identifies a LINE test delivery.</summary>
public sealed record LineTestDelivery(Guid Id);

/// <summary>LINE channel configuration without its secret values.</summary>
public sealed record LineChannelInfo(bool Enabled, bool HasAccessToken, bool HasChannelSecret, IReadOnlyList<LineGroupInfo> Groups, int AggregationSeconds);

/// <summary>A discovered LINE group and its approval status.</summary>
public sealed record LineGroupInfo(Guid Id, string ExternalId, string? Name, bool Approved, DateTimeOffset LastSeenAt);

/// <summary>Delivery status for an individual LINE message.</summary>
public sealed record LineDeliveryInfo(Guid Id, string Status, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>Recent LINE delivery status with its group name.</summary>
public sealed record LineDeliveryInfoSummary(Guid Id, string GroupName, string Status, string? FailureCode, DateTimeOffset CreatedAt);
