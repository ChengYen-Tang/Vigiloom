using System.Buffers;
using System.Net.Http.Headers;
using System.Text;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal enum HealthProbeFailure { None, Timeout, Transport, InvalidResponse, PolicyChanged, NetworkDenied }
internal readonly record struct HealthProbeResult(ReportedHealth? Health, HealthProbeFailure Failure);

/// <summary>Reads one bounded response using an already configured, restricted HTTP transport.</summary>
internal sealed class HealthProbe(HttpClient client)
{
    private const int MaxBytes = 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal async Task<HealthProbeResult> ReadAsync(Uri target, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!IsPlainText(response.Content.Headers.ContentType) || response.Content.Headers.ContentLength > MaxBytes)
                return Invalid();

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var rented = ArrayPool<byte>.Shared.Rent(MaxBytes + 1);
            try
            {
                var total = 0;
                while (total <= MaxBytes)
                {
                    var read = await stream.ReadAsync(rented.AsMemory(total, MaxBytes + 1 - total), deadline.Token);
                    if (read == 0) break;
                    total += read;
                }
                if (total > MaxBytes) return Invalid();
                var body = Utf8.GetString(rented, 0, total);
                var health = HealthResponse.Parse(response.StatusCode, body);
                return health is null ? Invalid() : new(health, HealthProbeFailure.None);
            }
            finally { ArrayPool<byte>.Shared.Return(rented, clearArray: true); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null, HealthProbeFailure.Timeout); }
        catch (HttpRequestException exception) when (exception.GetBaseException() is HealthNetworkDeniedException) { return new(null, HealthProbeFailure.NetworkDenied); }
        catch (HttpRequestException) { return new(null, HealthProbeFailure.Transport); }
        catch (IOException) { return new(null, HealthProbeFailure.Transport); }
        catch (DecoderFallbackException) { return Invalid(); }
    }

    private static bool IsPlainText(MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true &&
        (string.IsNullOrEmpty(contentType.CharSet) || contentType.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private static HealthProbeResult Invalid() => new(null, HealthProbeFailure.InvalidResponse);
}
