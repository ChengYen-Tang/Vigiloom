using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

var endpointText = Environment.GetEnvironmentVariable("VIGILOOM_URL");
var credentialsFile = Environment.GetEnvironmentVariable("VIGILOOM_AGENT_CREDENTIALS_FILE");
if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
    !int.TryParse(Environment.GetEnvironmentVariable("VIGILOOM_DURATION_SECONDS"), out var seconds) || seconds <= 0 ||
    string.IsNullOrWhiteSpace(credentialsFile))
    throw new InvalidOperationException("Set an HTTPS VIGILOOM_URL, positive VIGILOOM_DURATION_SECONDS, and VIGILOOM_AGENT_CREDENTIALS_FILE.");

var credentials = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(credentialsFile)) ?? [];
if (credentials.Length is not (30 or 100 or 300 or 500) || credentials.Any(string.IsNullOrWhiteSpace) ||
    credentials.Distinct(StringComparer.Ordinal).Count() != credentials.Length)
    throw new InvalidOperationException("Supply 30, 100, 300, or 500 distinct test Agent credentials.");

using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 100, AllowAutoRedirect = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var statusCounts = new ConcurrentDictionary<string, long>();
long attemptedPayloadBytes = 0;
long acceptedMetricPoints = 0;
long acceptedLogs = 0;
var senders = credentials.Select((credential, index) => RunAgentAsync(credential, index)).ToArray();
await Task.WhenAll(senders);
Console.WriteLine(JsonSerializer.Serialize(new { agents = credentials.Length, durationSeconds = seconds, attemptedPayloadBytes, acceptedMetricPoints, acceptedLogs, statusCounts }));

async Task RunAgentAsync(string credential, int index)
{
    var sourceId = Guid.CreateVersion7();
    await Task.WhenAll(SendPeriodicallyAsync(TimeSpan.FromSeconds(30), index, "metrics", CreateMetrics),
        SendPeriodicallyAsync(TimeSpan.FromSeconds(5), index, "logs", CreateLog));

    async Task SendPeriodicallyAsync(TimeSpan period, int position, string kind, Func<byte[]> createBatch)
    {
        try
        {
            await Task.Delay(period * position / credentials.Length, stop.Token);
            using var timer = new PeriodicTimer(period);
            do { await SendAsync(createBatch(), credential, kind); }
            while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    byte[] CreateMetrics()
    {
        var now = DateTimeOffset.UtcNow;
        var points = Enumerable.Range(0, 20).Select(core => new AgentMetricPoint(Guid.CreateVersion7(), now,
            AgentMetricKind.CpuCorePercent, "capacity-test", $"core-{core}", 25)).ToArray();
        return AgentMetricBatchCodec.Serialize(new(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, points));
    }

    byte[] CreateLog()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new AgentLogRecord(Guid.CreateVersion7(), now, new string('x', 512), null, null, null, null, null);
        return AgentLogBatchCodec.Serialize(new(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
            sourceId, AgentLogSourceKind.Journal, "capacity-test", 1, [record]));
    }
}

async Task SendAsync(byte[] bytes, string credential, string kind)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/v1/agent/batches"));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
    request.Content = new ByteArrayContent(bytes);
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    Interlocked.Add(ref attemptedPayloadBytes, bytes.Length);
    try
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        Count(((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            if (kind == "metrics") Interlocked.Add(ref acceptedMetricPoints, 20);
            else Interlocked.Increment(ref acceptedLogs);
        }
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { Count("cancelled"); }
    catch (HttpRequestException) { Count("connection_error"); }
    catch (TaskCanceledException) { Count("timeout"); }

    void Count(string status) => statusCounts.AddOrUpdate($"{kind}/{status}", 1, (_, old) => old + 1);
}
