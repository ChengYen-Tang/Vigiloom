namespace Vigiloom.Contracts.Identity;

public sealed record VerifyApiCredentialRequest(string? Value);
public sealed record VerifiedApiCredentialResponse(Guid Id, string Name, bool CanWrite);
public sealed record ValidateSessionRequest(Guid AccountId, string? SecurityStamp, bool AllowLockedOut = false);
public sealed record ValidatedSessionResponse(Guid AccountId, string UserName, string[] Roles);
public sealed record IdentityClaim(string Type, string Value, string ValueType, string Issuer, string OriginalIssuer);
public sealed record ValidatedCookieResponse(IdentityClaim[] Claims);
public sealed record PasswordLoginRequest(string UserName, string Password);
public sealed record PasswordLoginResponse(Guid AccountId, string SecurityStamp, bool RequiresTwoFactor, IdentityClaim[] Claims);
public sealed record CompleteTwoFactorRequest(Guid AccountId, string SecurityStamp, string Code, bool Recovery);
public sealed record IdentityActor(Guid AccountId, string SecurityStamp);
public sealed record CredentialListRequest(IdentityActor Actor, int Page);
public sealed record CredentialIssueRequest(IdentityActor Actor, string Name, bool CanWrite, int Days, string Password);
public sealed record CredentialRevokeRequest(IdentityActor Actor, Guid Id, string Password);
public sealed record AccountManagementRequest(IdentityActor Actor, Guid Id = default, int Page = 1, string? UserName = null, string? Role = null, string? Password = null);
