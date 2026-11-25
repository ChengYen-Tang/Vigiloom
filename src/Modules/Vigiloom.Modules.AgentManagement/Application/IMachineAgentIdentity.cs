namespace Vigiloom.Modules.AgentManagement.Application;

public interface IMachineAgentIdentity
{
    Task<EnrollmentResult> RedeemAsync(string? enrollmentSecret, byte[]? machineVerifier, CancellationToken token);
    Task<EnrollmentResult> RecoverAsync(string? enrollmentSecret, string? machineProof, CancellationToken token);
    Task<AgentPrincipal?> AuthenticateAsync(string? credential, CancellationToken token);
    Task<AgentIdentityStatus> BeginRotationAsync(string? credential, byte[]? newVerifier, CancellationToken token);
    Task<AgentIdentityStatus> FinishRotationAsync(string? credential, CancellationToken token);
}
