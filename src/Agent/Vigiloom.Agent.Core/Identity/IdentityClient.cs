using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Vigiloom.Agent.Core.Identity;

/// <summary>Registers and rotates machine credentials without putting secrets in command arguments or URLs.</summary>
public sealed class IdentityClient(HttpClient http)
{
    /// <summary>Replace only an unregistered pending identity after an administrator revoked its prior enrollment and binding.</summary>
    public async Task ResetPendingAsync(IdentityVault vault, Uri server, string enrollmentSecret, CancellationToken token)
    {
        var baseUri = NormalizeServer(server);
        if (!TryEnrollmentId(enrollmentSecret, out _)) throw new ArgumentException("Valid enrollment data is required.", nameof(enrollmentSecret));
        await using var session = vault.Acquire();
        var state = await session.ReadAsync(token) ?? throw new InvalidOperationException("No pending enrollment exists.");
        if (!string.Equals(state.Server, baseUri.AbsoluteUri, StringComparison.Ordinal) || state.AgentId is not null)
            throw new InvalidOperationException("A registered or different-server Agent identity cannot be reset as pending.");
        if (string.Equals(state.EnrollmentSecret, enrollmentSecret, StringComparison.Ordinal))
            throw new InvalidOperationException("Use newly issued enrollment data.");
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            await session.WriteAsync(new IdentityState { Server = baseUri.AbsoluteUri, EnrollmentSecret = enrollmentSecret, MachineSecret = Encode(secret) }, token);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public async Task<Guid> EnrollAsync(IdentityVault vault, Uri server, string? enrollmentSecret, CancellationToken token)
    {
        var baseUri = NormalizeServer(server);
        await using var session = vault.Acquire();
        var state = await session.ReadAsync(token);
        if (state is null)
        {
            if (!TryEnrollmentId(enrollmentSecret, out _)) throw new ArgumentException("Valid enrollment data is required.", nameof(enrollmentSecret));
            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                state = new IdentityState { Server = baseUri.AbsoluteUri, EnrollmentSecret = enrollmentSecret, MachineSecret = Encode(secret) };
                await session.WriteAsync(state, token);
            }
            finally { CryptographicOperations.ZeroMemory(secret); }
        }
        if (!string.Equals(state.Server, baseUri.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("Existing Agent identity belongs to another server.");
        if (state.AgentId is { } existing && existing != Guid.Empty) return existing;
        if (!TryEnrollmentId(state.EnrollmentSecret, out var enrollmentId)) throw new InvalidDataException("Pending enrollment data is missing or invalid.");
        if (enrollmentSecret is not null && !string.Equals(state.EnrollmentSecret, enrollmentSecret, StringComparison.Ordinal))
            throw new InvalidOperationException("A different enrollment is already pending; do not overwrite the existing machine identity.");
        var machine = Decode(state.MachineSecret);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/agent/enroll"))
            {
                Content = JsonContent.Create(new EnrollmentPayload(state.EnrollmentSecret!, SHA256.HashData(machine)))
            };
            using var response = await http.SendAsync(request, token);
            Guid agentId;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Enrollment may have committed before its response was lost and its initial window expired.
                using var recovery = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/agent/recover"))
                {
                    Content = JsonContent.Create(new RecoveryPayload(state.EnrollmentSecret!))
                };
                recovery.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vlr_{enrollmentId:D}.{state.MachineSecret}");
                using var recovered = await http.SendAsync(recovery, token);
                agentId = await ReadIdAsync(recovered, token);
            }
            else agentId = await ReadIdAsync(response, token);
            state.AgentId = agentId;
            state.EnrollmentSecret = null;
            await session.WriteAsync(state, token);
            return agentId;
        }
        finally { CryptographicOperations.ZeroMemory(machine); }
    }

    /// <summary>Persist the replacement before rotation, retaining both secrets until the server confirms it.</summary>
    public async Task RotateAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var baseUri = NormalizeServer(server);
        await using var session = vault.Acquire();
        var state = await session.ReadAsync(token) ?? throw new InvalidOperationException("The Agent is not registered.");
        if (!string.Equals(state.Server, baseUri.AbsoluteUri, StringComparison.Ordinal) || state.AgentId is not { Version: 7 } agentId)
            throw new InvalidOperationException("A registered identity for this server is required.");
        if (state.PendingSecret is null)
        {
            var generated = RandomNumberGenerator.GetBytes(32);
            try { state.PendingSecret = Encode(generated); await session.WriteAsync(state, token); }
            finally { CryptographicOperations.ZeroMemory(generated); }
        }
        var current = Decode(state.MachineSecret);
        byte[]? pending = null;
        try
        {
            pending = Decode(state.PendingSecret);
            using var start = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/agent/rotation/start"))
            {
                Content = JsonContent.Create(new RotationPayload(SHA256.HashData(pending)))
            };
            start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{agentId:D}.{state.MachineSecret}");
            using var startResponse = await http.SendAsync(start, token);
            if (!startResponse.IsSuccessStatusCode && startResponse.StatusCode != HttpStatusCode.Unauthorized)
                throw new InvalidOperationException($"Agent credential rotation could not start (HTTP {(int)startResponse.StatusCode}). Both credentials were retained.");
            // An old credential may have stopped working because finish committed but its response was lost.
            using var finish = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/v1/agent/rotation/finish"));
            finish.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{agentId:D}.{state.PendingSecret}");
            using var finishResponse = await http.SendAsync(finish, token);
            if (!finishResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"Agent credential rotation was not confirmed (HTTP {(int)finishResponse.StatusCode}). Both credentials were retained.");
            state.MachineSecret = state.PendingSecret;
            state.PendingSecret = null;
            await session.WriteAsync(state, token);
        }
        finally { CryptographicOperations.ZeroMemory(current); if (pending is not null) CryptographicOperations.ZeroMemory(pending); }
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Agent registration was rejected (HTTP {(int)response.StatusCode}). The local identity was retained.");
        var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: token);
        if (result?.AgentId is not { Version: 7 } id || id == Guid.Empty) throw new InvalidDataException("The server returned an invalid Agent identity.");
        return id;
    }

    internal static Uri NormalizeServer(Uri server)
    {
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(server.UserInfo) ||
            !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment))
            throw new ArgumentException("Agent server must be an absolute HTTPS URL without credentials, query or fragment.", nameof(server));
        return new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    internal static async Task<(IdentityState? State, bool ServerMismatch)> ReadRegisteredAsync(IdentityVault vault, Uri expected, CancellationToken token)
    {
        IdentityState? state;
        await using (var session = await vault.AcquireForTransportAsync(token)) state = await session.ReadAsync(token);
        if (state?.AgentId is not { Version: 7 }) return (null, false);
        if (!string.Equals(state.Server, expected.AbsoluteUri, StringComparison.Ordinal)) return (null, true);
        var decoded = Decode(state.MachineSecret);
        CryptographicOperations.ZeroMemory(decoded);
        return (state, false);
    }

    private static bool TryEnrollmentId(string? value, out Guid id)
    {
        id = default;
        return value is { Length: 84 } && value.StartsWith("vle_", StringComparison.Ordinal) && value[40] == '.' &&
            Guid.TryParseExact(value.AsSpan(4, 36), "D", out id) && id.Version == 7;
    }

    private static string Encode(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Decode(string data)
    {
        if (string.IsNullOrEmpty(data)) throw new InvalidDataException("Invalid machine credential.");
        try
        {
            var bytes = Convert.FromBase64String(data.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 ? bytes : throw new InvalidDataException("Invalid machine credential.");
        }
        catch (FormatException exception) { throw new InvalidDataException("Invalid machine credential.", exception); }
    }

    private sealed record EnrollmentPayload(string EnrollmentSecret, byte[] MachineVerifier);
    private sealed record RecoveryPayload(string EnrollmentSecret);
    private sealed record RotationPayload(byte[] MachineVerifier);
    private sealed record EnrollmentResponse(Guid AgentId);
}
