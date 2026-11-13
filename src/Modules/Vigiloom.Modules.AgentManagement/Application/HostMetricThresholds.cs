using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Manages threshold policies and evaluates a single exact metric series on demand.</summary>
public sealed class HostMetricThresholds(IDbContextFactory<AgentManagementDbContext> contexts, IDeviceAgentSource devices, TimeProvider clock)
{
    private const int MaximumPerDevice = 500;

    public async Task<HostMetricThresholdStatus?> GetAsync(Guid deviceId, Guid agentId, AgentMetricKind kind,
        string source, string? dimension, CancellationToken token)
    {
        ValidateSeries(deviceId, agentId, kind, source, dimension);
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Agents.AsNoTracking().AnyAsync(x => x.Id == agentId && x.DeviceId == deviceId, token)) return null;
        var hash = HashSeries(agentId, kind, source, dimension);
        var row = await db.HostMetricThresholds.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.SeriesHash == hash, token);
        var now = clock.GetUtcNow();
        if (row is null) return new(HostMetricThresholdState.NotConfigured, null, now, null, null);
        VerifySeries(row, agentId, kind, source, dimension);
        var view = ToView(row);
        if (!row.Enabled) return new(HostMetricThresholdState.Disabled, null, now, view, null);
        var sample = await db.HostMetrics.AsNoTracking()
            .Where(x => x.AgentId == agentId && x.Kind == kind &&
                EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2") == source && EF.Functions.DataLength(x.Source) == EF.Functions.DataLength(source) &&
                (dimension == null ? x.Dimension == null : x.Dimension != null &&
                    EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2") == dimension && EF.Functions.DataLength(x.Dimension) == EF.Functions.DataLength(dimension)))
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId)
            .Select(x => new HostMetricValue(x.OccurredAt, x.ReceivedAt, x.Kind, x.Source, x.Dimension, x.Value) { AgentId = x.AgentId })
            .FirstOrDefaultAsync(token);
        now = clock.GetUtcNow();
        if (sample is null) return new(HostMetricThresholdState.Unknown, HostMetricUnknownReason.NoSample, now, view, null);
        if (sample.OccurredAt > now) return new(HostMetricThresholdState.Unknown, HostMetricUnknownReason.FutureTimestamp, now, view, sample);
        if (now - sample.OccurredAt > TimeSpan.FromSeconds(row.FreshnessSeconds))
            return new(HostMetricThresholdState.Unknown, HostMetricUnknownReason.Stale, now, view, sample);
        var exceeded = row.Comparison switch
        {
            HostMetricComparison.AtLeast => sample.Value >= row.Value,
            HostMetricComparison.AtMost => sample.Value <= row.Value,
            _ => throw new InvalidDataException("Invalid saved threshold comparison.")
        };
        return new(exceeded ? HostMetricThresholdState.Exceeded : HostMetricThresholdState.Normal, null, now, view, sample);
    }

    public async Task<HostMetricThresholdView?> UpdateAsync(Guid deviceId, Guid agentId, AgentMetricKind kind,
        string source, string? dimension, HostMetricThresholdInput input, Guid actorId, CancellationToken token)
    {
        ValidateSeries(deviceId, agentId, kind, source, dimension);
        if (actorId == Guid.Empty || input is null || !Enum.IsDefined(input.Comparison) || !double.IsFinite(input.Value) ||
            input.Value < 0 || input.Value > AgentMetricBatchCodec.MaximumValue || input.FreshnessSeconds is < 30 or > 86400)
            throw new ArgumentException("Invalid metric threshold.");
        byte[] version;
        try { version = Convert.FromBase64String(input.Version ?? ""); }
        catch (FormatException) { return null; }
        if (version.Length is not (0 or 8)) return null;
        if (await devices.GetAsync(deviceId, token) is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        await AgentDeviceLock.AcquireAsync(db, deviceId, token);
        if (!await db.Agents.AnyAsync(x => x.Id == agentId && x.DeviceId == deviceId, token)) return null;
        var hash = HashSeries(agentId, kind, source, dimension);
        var row = await db.HostMetricThresholds.SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.SeriesHash == hash, token);
        if (row is null)
        {
            if (version.Length != 0) return null;
            if (await db.HostMetricThresholds.CountAsync(x => x.DeviceId == deviceId, token) >= MaximumPerDevice)
                throw new ArgumentException("Too many metric thresholds for this device.");
            row = new HostMetricThreshold { DeviceId = deviceId, AgentId = agentId, Kind = kind, Source = source, Dimension = dimension, SeriesHash = hash };
            db.HostMetricThresholds.Add(row);
        }
        else
        {
            VerifySeries(row, agentId, kind, source, dimension);
            if (version.Length != 8 || !version.AsSpan().SequenceEqual(row.Version)) return null;
            db.Entry(row).Property(x => x.Version).OriginalValue = version;
        }
        row.Enabled = input.Enabled;
        row.Comparison = input.Comparison;
        row.Value = input.Value;
        row.FreshnessSeconds = input.FreshnessSeconds;
        if (db.Entry(row).State != EntityState.Added) db.Entry(row).Property(x => x.Enabled).IsModified = true;
        try
        {
            await db.SaveChangesAsync(token);
            db.HostMetricThresholdAudits.Add(new HostMetricThresholdAudit { ThresholdId = row.Id, DeviceId = deviceId,
                ActorId = actorId, OccurredAt = clock.GetUtcNow(), Enabled = row.Enabled, Comparison = row.Comparison,
                Value = row.Value, FreshnessSeconds = row.FreshnessSeconds, Version = row.Version.ToArray() });
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return ToView(row);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return null; }
    }

    private static void ValidateSeries(Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension)
    {
        if (deviceId == Guid.Empty || agentId == Guid.Empty || !Enum.IsDefined(kind) || kind == AgentMetricKind.CollectionUnavailable ||
            string.IsNullOrWhiteSpace(source) || source.Length > 128 || source.Any(char.IsControl) ||
            dimension is { Length: > 256 } || dimension?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid metric series.");
    }

    private static byte[] HashSeries(Guid agentId, AgentMetricKind kind, string source, string? dimension)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[24];
        agentId.TryWriteBytes(header);
        BinaryPrimitives.WriteInt32BigEndian(header[16..], (int)kind);
        BinaryPrimitives.WriteInt32BigEndian(header[20..], source.Length);
        hash.AppendData(header);
        hash.AppendData(Encoding.Unicode.GetBytes(source));
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, dimension?.Length ?? -1);
        hash.AppendData(length);
        if (dimension is not null) hash.AppendData(Encoding.Unicode.GetBytes(dimension));
        return hash.GetHashAndReset();
    }

    private static void VerifySeries(HostMetricThreshold row, Guid agentId, AgentMetricKind kind, string source, string? dimension)
    {
        if (row.AgentId != agentId || row.Kind != kind || !string.Equals(row.Source, source, StringComparison.Ordinal) ||
            !string.Equals(row.Dimension, dimension, StringComparison.Ordinal))
            throw new InvalidDataException("Metric series identity does not match its saved threshold.");
    }

    private static HostMetricThresholdView ToView(HostMetricThreshold row) => new(row.Id, row.DeviceId, row.AgentId,
        row.Kind, row.Source, row.Dimension, row.Enabled, row.Comparison, row.Value, row.FreshnessSeconds, Convert.ToBase64String(row.Version));
}
