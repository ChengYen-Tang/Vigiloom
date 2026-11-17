namespace Vigiloom.Agent.Core.Identity;

/// <summary>Consumes one installer-provided enrollment secret under the Agent service identity.</summary>
public sealed class AgentEnrollmentBootstrap(IdentityClient identity)
{
    public const string FileName = "enrollment.pending";

    /// <returns>False when there is no installer enrollment to process.</returns>
    public async Task<bool> TryOnceAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        string? secret;
        await using (var session = await vault.AcquireForTransportAsync(token))
        {
            secret = await session.ReadBootstrapEnrollmentAsync(token);
            if (secret is null) return false;
            var state = await session.ReadAsync(token);
            if (state is not null && !string.Equals(state.Server, expected.AbsoluteUri, StringComparison.Ordinal))
                throw new InvalidOperationException("The Agent identity belongs to another server.");
            if (state?.AgentId is not null)
            {
                // An earlier attempt may have committed before the bootstrap file was removed.
                session.DeleteBootstrapEnrollment();
                return true;
            }
            if (state?.EnrollmentSecret is not null && !string.Equals(state.EnrollmentSecret, secret, StringComparison.Ordinal))
                throw new InvalidOperationException("A different Agent enrollment is already pending.");
        }

        var agentId = await identity.EnrollAsync(vault, expected, secret, token);
        await using (var session = await vault.AcquireForTransportAsync(token))
        {
            var state = await session.ReadAsync(token);
            if (state?.AgentId != agentId || !string.Equals(state.Server, expected.AbsoluteUri, StringComparison.Ordinal))
                throw new InvalidOperationException("The registered Agent identity changed before bootstrap cleanup.");
            session.DeleteBootstrapEnrollment();
        }
        return true;
    }
}
