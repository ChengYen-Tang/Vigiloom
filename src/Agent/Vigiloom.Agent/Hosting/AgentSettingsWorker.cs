using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Agent.Hosting.Logs;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Agent.Hosting.Integrity;

namespace Vigiloom.Agent.Hosting;

/// <summary>Restores local settings and periodically synchronizes them without blocking other workers.</summary>
internal sealed class AgentSettingsWorker(AgentSettingsClient client, AgentQueueCapacityClient capacityClient, AgentMetricSettingsClient metricClient, AgentLogSettingsClient logClient, AgentInventorySettingsClient inventoryClient, AgentIntegritySettingsClient integrityClient, AgentSettingsRuntime runtime, AgentQueueCapacityRuntime capacity, AgentMetricSettingsRuntime metrics, AgentLogSettingsRuntime logs, InventorySettingsRuntime inventory, AgentIntegritySettingsRuntime integrity, IConfiguration configuration,
    TimeProvider clock, ILogger<AgentSettingsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var directory = configuration["Vigiloom:Agent:StateDirectory"];
            var server = configuration["Vigiloom:Agent:Server"];
            if (string.IsNullOrWhiteSpace(directory) || !Uri.TryCreate(server, UriKind.Absolute, out var uri))
            {
                runtime.Bind(null);
                capacity.Bind(null);
                metrics.Bind(null);
                logs.Bind(null);
                inventory.Bind(null);
                integrity.Bind(null);
                if (!await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken)) break;
                continue;
            }
            try
            {
                var vault = new IdentityVault(directory);
                await using var ownership = vault.AcquireSettingsOwnership();
                AgentSettingsOwner? owner = null;
                if (!IsCurrent(directory, server)) continue;
                runtime.Bind(null);
                capacity.Bind(null);
                metrics.Bind(null);
                logs.Bind(null);
                inventory.Bind(null);
                integrity.Bind(null);
                do
                {
                    if (!IsCurrent(directory, server)) break;
                    try
                    {
                        owner = await AgentSettingsClient.GetOwnerAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        runtime.Bind(owner);
                        capacity.Bind(owner);
                        metrics.Bind(owner);
                        logs.Bind(owner);
                        inventory.Bind(owner);
                        integrity.Bind(owner);
                        if (owner is not null)
                        {
                            if (runtime.NeedsRestore(owner)) await RestoreAsync(
                                async () => (await client.LoadSavedAsync(vault, uri, stoppingToken)).Settings,
                                saved => runtime.RestoreIfEmpty(owner, saved), () => runtime.MarkFailure(owner, AgentConfigurationFailure.LocalStorage), directory, server, stoppingToken);
                            if (metrics.NeedsRestore(owner)) await RestoreAsync(
                                async () => (await metricClient.LoadSavedAsync(vault, uri, stoppingToken)).Settings,
                                saved => metrics.RestoreIfEmpty(owner, saved), () => metrics.MarkFailure(owner, AgentConfigurationFailure.LocalStorage), directory, server, stoppingToken);
                            if (inventory.NeedsRestore(owner)) await RestoreAsync(
                                async () => (await inventoryClient.LoadSavedAsync(vault, uri, stoppingToken)).Settings,
                                saved => inventory.RestoreIfEmpty(owner, saved), () => inventory.MarkFailure(owner, AgentConfigurationFailure.LocalStorage), directory, server, stoppingToken);
                            if (logs.NeedsRestore(owner)) await RestoreAsync(
                                async () => (await logClient.LoadSavedAsync(vault, uri, stoppingToken)).Settings,
                                saved => logs.RestoreIfEmpty(owner, saved), () => logs.MarkFailure(owner, AgentConfigurationFailure.LocalStorage), directory, server, stoppingToken);
                            if (integrity.NeedsRestore(owner)) await RestoreAsync(
                                async () => (await integrityClient.LoadSavedAsync(vault, uri, stoppingToken)).Settings,
                                saved => integrity.RestoreIfEmpty(owner, saved), () => integrity.MarkFailure(owner, AgentConfigurationFailure.LocalStorage), directory, server, stoppingToken);
                        }
                        var result = await client.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (result.Settings is not null) runtime.Publish(owner, result.Settings);
                        if (result.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) runtime.Bind(null);
                        var failure = result.Status switch
                        {
                            AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                            AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                            AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                            AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                            _ => (AgentConfigurationFailure?)null
                        };
                        if (failure is { } failureCode) runtime.MarkFailure(owner, failureCode);
                        if (result.Status is AgentSettingsSyncStatus.Invalid or AgentSettingsSyncStatus.Conflict)
                            logger.LogWarning("Agent configuration synchronization: {Status}.", result.Status);
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        runtime.MarkFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent configuration synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    try
                    {
                        if (!IsCurrent(directory, server)) break;
                        var capacityResult = await capacityClient.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (capacityResult.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) capacity.Bind(null);
                        else capacity.SetFailure(owner, capacityResult.Status switch
                        {
                            AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                            AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                            AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                            AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                            _ => null
                        });
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        capacity.SetFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent queue capacity synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    try
                    {
                        if (!IsCurrent(directory, server)) break;
                        var metricResult = await metricClient.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (metricResult.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) metrics.Bind(null);
                        else if (metricResult.Settings is not null) metrics.Publish(owner, metricResult.Settings);
                        else
                        {
                            var failure = metricResult.Status switch
                            {
                                AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                                AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                                AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                                AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                                _ => (AgentConfigurationFailure?)null
                            };
                            if (failure is { } failureCode) metrics.MarkFailure(owner, failureCode);
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        metrics.MarkFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent metric settings synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    try
                    {
                        if (!IsCurrent(directory, server)) break;
                        var result = await inventoryClient.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (result.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) inventory.Bind(null);
                        else if (result.Settings is not null) inventory.Publish(owner, result.Settings);
                        else
                        {
                            var failure = result.Status switch
                            {
                                AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                                AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                                AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                                AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                                _ => (AgentConfigurationFailure?)null
                            };
                            if (failure is { } failureCode) inventory.MarkFailure(owner, failureCode);
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        inventory.MarkFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent inventory settings synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    try
                    {
                        if (!IsCurrent(directory, server)) break;
                        var logResult = await logClient.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (logResult.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) logs.Bind(null);
                        else if (logResult.Settings is not null) logs.Publish(owner, logResult.Settings);
                        else
                        {
                            var failure = logResult.Status switch
                            {
                                AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                                AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                                AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                                AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                                _ => (AgentConfigurationFailure?)null
                            };
                            if (failure is { } failureCode) logs.MarkFailure(owner, failureCode);
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        logs.MarkFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent log settings synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    try
                    {
                        if (!IsCurrent(directory, server)) break;
                        var result = await integrityClient.SyncAsync(vault, uri, stoppingToken);
                        if (!IsCurrent(directory, server)) break;
                        if (result.Status is AgentSettingsSyncStatus.NotRegistered or AgentSettingsSyncStatus.ServerMismatch) integrity.Bind(null);
                        else if (result.Settings is not null) integrity.Publish(owner, result.Settings);
                        else
                        {
                            var failure = result.Status switch
                            {
                                AgentSettingsSyncStatus.Unavailable => AgentConfigurationFailure.Unavailable,
                                AgentSettingsSyncStatus.Invalid => AgentConfigurationFailure.Invalid,
                                AgentSettingsSyncStatus.Unauthorized => AgentConfigurationFailure.Unauthorized,
                                AgentSettingsSyncStatus.Conflict => AgentConfigurationFailure.Conflict,
                                _ => (AgentConfigurationFailure?)null
                            };
                            if (failure is { } failureCode) integrity.MarkFailure(owner, failureCode);
                        }
                    }
                    catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
                    {
                        integrity.MarkFailure(owner, AgentConfigurationFailure.LocalStorage);
                        logger.LogWarning("Agent integrity settings synchronization failed: {ErrorType}.", ex.GetType().Name);
                    }
                    var seconds = runtime.Current.SettingsPollSeconds * (0.8 + Random.Shared.NextDouble() * 0.4);
                    if (!await DelayAsync(TimeSpan.FromSeconds(seconds), stoppingToken)) return;
                } while (IsCurrent(directory, server));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (IsRecoverable(ex, stoppingToken))
            {
                logger.LogWarning("Agent configuration synchronizer unavailable: {ErrorType}.", ex.GetType().Name);
                if (!await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken)) break;
            }
        }
    }

    private async Task RestoreAsync<T>(Func<Task<T?>> load, Action<T> publish, Action markFailure,
        string directory, string server, CancellationToken token) where T : class
    {
        try
        {
            var saved = await load();
            if (!IsCurrent(directory, server)) return;
            if (saved is not null) publish(saved);
        }
        catch (Exception ex) when (IsRecoverable(ex, token))
        {
            markFailure();
            logger.LogWarning("Agent saved settings could not be loaded: {ErrorType}.", ex.GetType().Name);
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, clock, token); return true; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
    }

    private bool IsCurrent(string directory, string server) =>
        string.Equals(directory, configuration["Vigiloom:Agent:StateDirectory"], StringComparison.Ordinal) &&
        string.Equals(server, configuration["Vigiloom:Agent:Server"], StringComparison.Ordinal);

    private static bool IsRecoverable(Exception ex, CancellationToken token) =>
        ex is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or ArgumentException or
            UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or System.Data.Common.DbException ||
        ex is OperationCanceledException && !token.IsCancellationRequested;
}
