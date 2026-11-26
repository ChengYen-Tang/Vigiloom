using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Vigiloom.Contracts.Resources;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

/// <summary>Reads the resource service over its internal API; the SQL transaction is local to network monitoring.</summary>
public sealed class RemoteDevicePingSource(HttpClient client) : IDevicePingSource
{
    public async IAsyncEnumerable<PingDeviceSnapshot> ReadEnabledAsync([EnumeratorCancellation] CancellationToken token)
    {
        using var response = await client.GetAsync("internal/v1/ping-targets", HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await foreach (var item in response.Content.ReadFromJsonAsAsyncEnumerable<PingDeviceSnapshot>(cancellationToken: token))
            if (item is not null) yield return item;
    }

    public async Task<bool> IsCurrentAsync(Guid id, byte[] version, string target, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/v1/ping-targets/verify", new PingTargetVerification(id, version, target), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<PingDeviceContext> GetContextAsync(Guid id, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
        await client.GetFromJsonAsync<PingDeviceContext>($"internal/v1/ping-targets/{id:D}/context", token)
        ?? throw new InvalidOperationException("The resource service returned an empty device context.");
}
