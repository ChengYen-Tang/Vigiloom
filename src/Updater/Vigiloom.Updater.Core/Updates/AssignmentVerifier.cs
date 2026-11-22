using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Updates;

/// <summary>Rechecks an authenticated Agent's assignment while its service is stopped.</summary>
/// <remarks>The caller supplies the in-memory Agent credential and an HttpClient with redirects disabled.
/// Neither this class nor the journal persists the credential.</remarks>
public sealed class AssignmentVerifier
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly AuthenticationHeaderValue authorization;
    private readonly AgentVersionTarget expected;
    private readonly PackageManifest manifest;
    private readonly TimeProvider clock;

    public AssignmentVerifier(HttpClient http, Uri server, string credential, AgentVersionTarget expected,
        PackageManifest manifest, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!server.IsAbsoluteUri || server.Scheme != Uri.UriSchemeHttps || server.UserInfo.Length != 0 ||
            server.Query.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("An HTTPS Agent server without credentials, query or fragment is required.", nameof(server));
        if (!expected.IsValid() || expected.PackageVersion != manifest.Version || expected.RuntimeIdentifier != manifest.RuntimeIdentifier ||
            expected.ManifestSha256 != manifest.ManifestSha256)
            throw new ArgumentException("The expected assignment does not match the verified manifest.", nameof(expected));
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.expected = expected;
        this.manifest = manifest;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        endpoint = new Uri(new Uri(server.AbsoluteUri.TrimEnd('/') + "/"), "api/v1/agent/version-assignment");
        authorization = new AuthenticationHeaderValue("Bearer", credential);
    }

    public async Task<UpdateAuthorization> CheckAsync(Guid updateId, CancellationToken token)
    {
        if (updateId != expected.UpdateId) return UpdateAuthorization.NoLongerAuthorized;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = authorization;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NoContent) return UpdateAuthorization.NoLongerAuthorized;
            if (response.StatusCode != HttpStatusCode.OK) return UpdateAuthorization.Unavailable;
            await response.Content.LoadIntoBufferAsync(4096, timeout.Token);
            var current = await response.Content.ReadFromJsonAsync<AgentVersionTarget>(cancellationToken: timeout.Token);
            if (current?.IsValid() != true) return UpdateAuthorization.Unavailable;
            return current == expected ? UpdateAuthorization.Authorized : UpdateAuthorization.NoLongerAuthorized;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return UpdateAuthorization.Unavailable; }
        catch (HttpRequestException) { return UpdateAuthorization.Unavailable; }
        catch (JsonException) { return UpdateAuthorization.Unavailable; }
    }
}
