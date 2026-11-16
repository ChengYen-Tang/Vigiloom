using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Updates;

public enum AgentVersionFetchStatus { Available, NoAssignment, NotRegistered, ServerMismatch, Unauthorized, Unavailable, Invalid }
public sealed record AgentVersionFetch(AgentVersionFetchStatus Status, AgentVersionTarget? Target = null, Guid? AgentId = null);

/// <summary>Fetches only the desired version for the locally registered Agent.</summary>
public sealed class AgentVersionClient(HttpClient http, TimeProvider clock)
{
    public async Task<AgentVersionFetch> GetAsync(IdentityVault vault, Uri server, CancellationToken token)
    {
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, mismatch) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity is null) return new(mismatch ? AgentVersionFetchStatus.ServerMismatch : AgentVersionFetchStatus.NotRegistered);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(expected, "api/v1/agent/version-assignment"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{identity.AgentId!.Value:D}.{identity.MachineSecret}");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent) return new(AgentVersionFetchStatus.NoAssignment);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return new(AgentVersionFetchStatus.Unauthorized);
            if (response.StatusCode != HttpStatusCode.OK) return new(AgentVersionFetchStatus.Unavailable);
            await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
            var target = await response.Content.ReadFromJsonAsync<AgentVersionTarget>(cancellationToken: timeout.Token);
            return target?.IsValid() == true ? new(AgentVersionFetchStatus.Available, target, identity.AgentId) : new(AgentVersionFetchStatus.Invalid);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(AgentVersionFetchStatus.Unavailable); }
        catch (HttpRequestException) { return new(AgentVersionFetchStatus.Unavailable); }
        catch (JsonException) { return new(AgentVersionFetchStatus.Invalid); }
    }

    /// <summary>Reads the current vault only after a target was fetched for this same Agent.</summary>
    public async Task<UpdateHandoff?> CreateHandoffAsync(IdentityVault vault, Uri server, AgentVersionFetch fetched, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(fetched);
        if (fetched.Status != AgentVersionFetchStatus.Available || fetched.Target?.IsValid() != true || fetched.AgentId is not { Version: 7 })
            return null;
        var expected = IdentityClient.NormalizeServer(server);
        var (identity, _) = await IdentityClient.ReadRegisteredAsync(vault, expected, token);
        if (identity?.AgentId != fetched.AgentId) return null;
        var handoff = new UpdateHandoff { UpdateId = fetched.Target.UpdateId, Credential = $"vla_{identity.AgentId:D}.{identity.MachineSecret}" };
        return handoff.IsValid() ? handoff : null;
    }
}
