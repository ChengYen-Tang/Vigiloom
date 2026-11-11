namespace Vigiloom.Contracts.Agent;

public enum PackageCredentialAction { Keep = 0, Replace = 1, Clear = 2 }

/// <summary>Publisher source settings without the stored credential.</summary>
public sealed record AgentPackageSourceView(string BaseUri, bool HasCredential, string Version);

/// <summary>Updates the publisher source; credentials are write-only.</summary>
public sealed record AgentPackageSourceUpdate(string BaseUri, PackageCredentialAction CredentialAction, string? Credential, string Version);
