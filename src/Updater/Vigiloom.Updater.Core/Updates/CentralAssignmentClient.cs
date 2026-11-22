using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Updates;

public enum CurrentAssignmentStatus { Available, Changed, Unauthorized, WrongDevice, Unavailable }
public sealed record CurrentAssignment(CurrentAssignmentStatus Status, AgentVersionTarget? Target = null);

/// <summary>Resolves a local handoff against the central identity and current assignment.</summary>
/// <remarks>The server and expected device ID must come from protected installation settings.
/// The supplied HttpClient must disable redirects and cookies.</remarks>
public sealed class CentralAssignmentClient(HttpClient http, TimeProvider clock)
{
    public async Task<CurrentAssignment> GetAsync(Uri server, Guid expectedDeviceId, UpdateHandoff request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid()) return new(CurrentAssignmentStatus.Unauthorized);
        var assignment = await GetCurrentAsync(server, expectedDeviceId, request.Credential, token);
        return assignment is { Status: CurrentAssignmentStatus.Available, Target: not null } &&
            assignment.Target.UpdateId != request.UpdateId ? new(CurrentAssignmentStatus.Changed) : assignment;
    }

    /// <summary>Reads the current assignment after validating the authenticated Agent and device.</summary>
    public async Task<CurrentAssignment> GetCurrentAsync(Uri server, Guid expectedDeviceId, string credential, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("An HTTPS Agent server without credentials, query or fragment is required.", nameof(server));
        if (expectedDeviceId.Version != 7 || !UpdateHandoff.TryGetAgentId(credential, out var agentId))
            return new(CurrentAssignmentStatus.Unauthorized);
        var origin = new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var identityRequest = Request(new Uri(origin, "api/v1/agent/identity"), credential);
            using var identityResponse = await http.SendAsync(identityRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (identityResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(CurrentAssignmentStatus.Unauthorized);
            if (identityResponse.StatusCode != HttpStatusCode.OK) return new(CurrentAssignmentStatus.Unavailable);
            await identityResponse.Content.LoadIntoBufferAsync(4096, timeout.Token);
            var identity = await identityResponse.Content.ReadFromJsonAsync<MachineIdentity>(cancellationToken: timeout.Token);
            if (identity is null || identity.AgentId != agentId || identity.DeviceId != expectedDeviceId) return new(CurrentAssignmentStatus.WrongDevice);

            using var assignmentRequest = Request(new Uri(origin, "api/v1/agent/version-assignment"), credential);
            using var assignmentResponse = await http.SendAsync(assignmentRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (assignmentResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(CurrentAssignmentStatus.Unauthorized);
            if (assignmentResponse.StatusCode == HttpStatusCode.NoContent) return new(CurrentAssignmentStatus.Changed);
            if (assignmentResponse.StatusCode != HttpStatusCode.OK) return new(CurrentAssignmentStatus.Unavailable);
            await assignmentResponse.Content.LoadIntoBufferAsync(4096, timeout.Token);
            var target = await assignmentResponse.Content.ReadFromJsonAsync<AgentVersionTarget>(cancellationToken: timeout.Token);
            if (target?.IsValid() != true) return new(CurrentAssignmentStatus.Unavailable);
            return new(CurrentAssignmentStatus.Available, target);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(CurrentAssignmentStatus.Unavailable); }
        catch (HttpRequestException) { return new(CurrentAssignmentStatus.Unavailable); }
        catch (JsonException) { return new(CurrentAssignmentStatus.Unavailable); }
    }

    private static HttpRequestMessage Request(Uri uri, string credential) => new(HttpMethod.Get, uri)
    { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", credential) } };

    private sealed record MachineIdentity(Guid AgentId, Guid DeviceId);
}
