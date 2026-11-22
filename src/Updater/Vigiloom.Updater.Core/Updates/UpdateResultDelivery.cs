using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Delivers retained outcomes using the current Agent credential.</summary>
/// <remarks>The caller supplies a client with redirects and cookies disabled and a verified installation server.</remarks>
public sealed class UpdateResultDelivery(HttpClient http, UpdateJournal journal, TimeProvider clock)
{
    private const int MaximumPerSync = 20;

    /// <summary>Reports a recent intermediate journal step on a best-effort basis; terminal outcomes use the durable outbox.</summary>
    public async Task ReportProgressAsync(Uri server, string credential, CancellationToken token)
    {
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0 || !UpdateHandoff.TryGetAgentId(credential, out var agentId))
            throw new ArgumentException("An HTTPS server and valid Agent credential are required.");
        var state = await journal.ReadAsync(token);
        if (state?.Provenance is not { } target || target.AgentId != agentId || state.Steps.Count == 0) return;
        var step = state.Steps[^1];
        if (step.MessageId is not { } messageId || messageId.Version != 7 || step.Phase != state.Phase) return;
        AgentUpdateResultKind? phase = state.Phase switch
        {
            UpdatePhase.Prepared => AgentUpdateResultKind.Prepared,
            UpdatePhase.Stopping => AgentUpdateResultKind.Stopping,
            UpdatePhase.Switching => AgentUpdateResultKind.Switching,
            UpdatePhase.Starting => AgentUpdateResultKind.Starting,
            UpdatePhase.Verifying => AgentUpdateResultKind.Verifying,
            UpdatePhase.RollingBack => AgentUpdateResultKind.RollingBack,
            _ => null
        };
        if (phase is null) return;
        var report = new AgentUpdateResult(messageId, state.Id, target.PackageId, target.ManifestSha256,
            state.Steps.Count, phase.Value, step.At.ToUniversalTime(), false, null);
        if (!report.IsValid()) throw new InvalidDataException("The retained update progress is invalid.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), "api/v1/agent/update-results"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", credential) },
            Content = JsonContent.Create(report)
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
    }

    public async Task<int> FlushAsync(Uri server, string credential, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0 || !UpdateHandoff.TryGetAgentId(credential, out var agentId))
            throw new ArgumentException("An HTTPS server and valid Agent credential are required.");
        var endpoint = new Uri(new Uri(server.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute), "api/v1/agent/update-results");
        var pending = await journal.ReadPendingAsync(agentId, MaximumPerSync, token);
        var acknowledged = 0;
        foreach (var item in pending)
        {
            var report = item.ToReport();
            if (!report.IsValid()) throw new InvalidDataException("A retained update outcome is invalid.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", credential) },
                Content = JsonContent.Create(report)
            };
            try
            {
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.NoContent) break;
                await journal.AcknowledgeAsync(agentId, item.MessageId, token);
                acknowledged++;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (HttpRequestException) { break; }
            catch (IOException) { break; }
        }
        return acknowledged;
    }
}
