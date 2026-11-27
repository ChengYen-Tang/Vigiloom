namespace Vigiloom.Contracts.Agent;

/// <summary>One-time enrollment data and the digest of the locally generated machine secret.</summary>
public sealed record EnrollmentRequest(string? EnrollmentSecret, byte[]? MachineVerifier);
public sealed record EnrollmentRecoveryRequest(string? EnrollmentSecret);
public sealed record RotationRequest(byte[]? MachineVerifier);
public sealed record EnrollmentResponse(Guid AgentId);
public sealed record EnrollmentSecretResponse(string Secret, DateTimeOffset ExpiresAt);
public sealed record AgentIdentityResponse(Guid AgentId, Guid DeviceId);
