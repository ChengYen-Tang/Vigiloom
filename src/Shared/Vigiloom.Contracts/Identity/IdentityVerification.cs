namespace Vigiloom.Contracts.Identity;

public sealed record VerifyApiCredentialRequest(string? Value);
public sealed record VerifiedApiCredentialResponse(Guid Id, string Name, bool CanWrite);
public sealed record ValidateSessionRequest(Guid AccountId, string? SecurityStamp, bool AllowLockedOut = false);
public sealed record ValidatedSessionResponse(Guid AccountId, string UserName, string[] Roles);
