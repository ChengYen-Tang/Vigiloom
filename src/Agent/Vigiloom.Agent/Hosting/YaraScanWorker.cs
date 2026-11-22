using System.Runtime.InteropServices;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Security;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Security;

namespace Vigiloom.Agent.Hosting;

/// <summary>Fetches one requested scan at a time and retries reporting until acknowledged.</summary>
internal sealed class YaraScanWorker(AgentYaraClient client, AgentSettingsRuntime settings, AgentMetricSettingsRuntime metrics,
    SamplingBudgetCoordinator budget, IConfiguration configuration,
    TimeProvider clock, ILogger<YaraScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        (AgentSettingsOwner Owner, QueuedBatch Batch)? pending = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var directory = configuration["Vigiloom:Agent:StateDirectory"];
            var server = configuration["Vigiloom:Agent:Server"];
            if (!string.IsNullOrWhiteSpace(directory) && Uri.TryCreate(server, UriKind.Absolute, out var uri))
            {
                try
                {
                    var vault = new IdentityVault(directory);
                    await using var ownership = vault.AcquireYaraOwnership();
                    var owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                    if (!IsCurrent(directory, server) || owner is null || pending is { } old && old.Owner != owner) pending = null;
                    if (owner is not null && pending is null && await client.NextAsync(vault, uri, stoppingToken) is { } job &&
                        IsCurrent(directory, server) && job.AgentId == owner.AgentId && await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken) == owner)
                    {
                        var queue = new DurableBatchQueue(vault, owner.AgentId);
                        if (!await queue.ContainsForOwnerAsync(job.Id, owner, stoppingToken) && IsCurrent(directory, server) &&
                            await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken) == owner)
                        {
                            var result = await ScanWithCancellationAsync(vault, uri, directory, job, owner, stoppingToken);
                            if (result is not null)
                            {
                                var now = clock.GetUtcNow();
                                var batch = new YaraResultBatch(YaraResultBatchCodec.SchemaVersion, job.Id, now, result);
                                pending = (owner, new(job.Id, now, 1, YaraResultBatchCodec.Serialize(batch)));
                            }
                        }
                    }
                    if (pending is { } current && owner == current.Owner && IsCurrent(directory, server))
                    {
                        var queue = new DurableBatchQueue(vault, owner.AgentId);
                        var result = await queue.EnqueueManyForOwnerAsync([current.Batch], owner, stoppingToken);
                        if (result is EnqueueResult.Queued or EnqueueResult.AlreadyQueued or EnqueueResult.OwnerChanged) pending = null;
                        else logger.LogWarning("YARA result could not be queued: {Status}.", result);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
                {
                    logger.LogWarning("YARA scan or result delivery failed: {ErrorType}.", error.GetType().Name);
                }
            }
            var seconds = settings.Current.SettingsPollSeconds * (0.8 + Random.Shared.NextDouble() * 0.4);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private bool IsCurrent(string directory, string server) =>
        string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) &&
        string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal);

    private async Task<YaraScanReport?> ScanWithCancellationAsync(IdentityVault vault, Uri server, string directory,
        YaraScanJob job, AgentSettingsOwner owner, CancellationToken stoppingToken)
    {
        using var scanStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var pollingStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorCancellationAsync();
        try { return await ScanAsync(directory, job, owner, scanStop.Token); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && scanStop.IsCancellationRequested) { return null; }
        finally
        {
            await pollingStop.CancelAsync();
            try { await monitor; } catch (OperationCanceledException) when (pollingStop.IsCancellationRequested) { }
        }

        async Task MonitorCancellationAsync()
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, pollingStop.Token);
                try
                {
                    var next = await client.NextAsync(vault, server, pollingStop.Token);
                    if (next?.Id != job.Id) { await scanStop.CancelAsync(); return; }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
                { logger.LogWarning("Could not check YARA cancellation: {ErrorType}.", ex.GetType().Name); }
                catch (OperationCanceledException) when (!pollingStop.IsCancellationRequested)
                { logger.LogWarning("Checking YARA cancellation timed out."); }
            }
        }
    }

    private async Task<YaraScanReport?> ScanAsync(string directory, YaraScanJob job, AgentSettingsOwner owner, CancellationToken token)
    {
        var request = job.Request;
        if (job.Id.Version != 7 || request is null || string.IsNullOrWhiteSpace(request.FilePath) ||
            !Path.IsPathFullyQualified(request.FilePath) || string.IsNullOrWhiteSpace(request.Rules) || request.Rules.Length > 65536 ||
            request.MaximumBytes is <= 0 or > 1073741824 || request.TimeoutSeconds is < 1 or > 3600)
            return new(YaraScanOutcome.Failed, "", "Invalid scan request.", false, clock.GetUtcNow());
        using var resources = new MetricResourceMonitor();
        await using (var lease = await budget.AcquireAsync(token))
        {
            var policy = metrics.ForOwner(owner);
            if (policy is null) return null;
            budget.Cpu.SetAllowance(TimeSpan.FromMilliseconds(policy.CpuMillisecondsPerMinute));
            budget.Io.SetAllowance((ulong)policy.IoLimitMiBPerMinute * 1024 * 1024);
            if (budget.Cpu.Delay() > TimeSpan.Zero || budget.Io.Delay() > TimeSpan.Zero ||
                SamplingMemoryBudget.IsExceeded(resources.ReadWorkingSetBytes(), (long)policy.WorkingSetLimitMiB * 1024 * 1024)) return null;
        }
        var work = Path.Combine(directory, "yara-scans");
        Directory.CreateDirectory(work);
        foreach (var path in Directory.EnumerateFiles(work, "vigiloom-*.yar"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (Guid.TryParseExact(name.AsSpan("vigiloom-".Length), "D", out var oldId) && oldId.Version == 7) File.Delete(path);
        }
        var rules = Path.Combine(work, $"vigiloom-{job.Id:D}.yar");
        try
        {
            await File.WriteAllTextAsync(rules, request.Rules, token);
            var name = OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "yara32.exe" : "yara64.exe" : "yara";
            var installed = Path.Combine(AppContext.BaseDirectory, name);
            var result = await YaraScanner.ScanAsync(File.Exists(installed) ? installed : name, rules, request.FilePath,
                request.MaximumBytes, TimeSpan.FromSeconds(request.TimeoutSeconds), token);
            await using (var lease = await budget.AcquireAsync(token))
            {
                if (result.CpuTime is { } cpu) budget.Cpu.Record(TimeSpan.Zero, cpu);
                else if (result.Started) budget.Cpu.MarkUnknown();
                budget.Io.Record(new ProcessIoSnapshot(0, 0), new ProcessIoSnapshot((ulong)Math.Max(0, result.InputBytes), 0));
            }
            return new(result.Status switch
            {
                YaraScanStatus.Completed => YaraScanOutcome.Completed, YaraScanStatus.FileTooLarge => YaraScanOutcome.FileTooLarge,
                YaraScanStatus.TimedOut => YaraScanOutcome.TimedOut, YaraScanStatus.Unavailable => YaraScanOutcome.Unavailable,
                _ => YaraScanOutcome.Failed
            }, result.Output, result.Error, result.OutputTruncated, clock.GetUtcNow());
        }
        finally { if (File.Exists(rules)) File.Delete(rules); }
    }
}
