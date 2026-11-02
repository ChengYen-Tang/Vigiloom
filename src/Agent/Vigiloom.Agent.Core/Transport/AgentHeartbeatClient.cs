using System.Net;
using System.Net.Http.Headers;
using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Sends a heartbeat using only a registered identity bound to the configured HTTPS server.</summary>
public sealed class AgentHeartbeatClient(HttpClient http)
{
    public async Task<HeartbeatResult> SendAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (state, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (state is null) return mismatch ? HeartbeatResult.ServerMismatch : HeartbeatResult.NotRegistered;
        var id = state.AgentId!.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(expected, "api/v1/agent/heartbeat"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{id:D}.{state.MachineSecret}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return response.StatusCode switch
        {
            HttpStatusCode.NoContent => HeartbeatResult.Accepted,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => HeartbeatResult.Unauthorized,
            _ => HeartbeatResult.Unavailable
        };
    }
}

public enum HeartbeatResult { Accepted, NotRegistered, ServerMismatch, Unauthorized, Unavailable }
