namespace Vigiloom.Contracts.Agent;

/// <summary>The fingerprint identifies the currently accepted root, not the publisher's identity.</summary>
public sealed record AgentPackageTrustView(bool Initialized, int? RootVersion, string? Sha256);

/// <summary>The fingerprint must be obtained independently of the publisher endpoint.</summary>
public sealed record AgentPackageTrustRequest(byte[] Root, string Sha256);
