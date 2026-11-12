using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>The bounded policy delivered to one authenticated Agent.</summary>
public sealed record IntegritySettingsSnapshot(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] string Revision,
    [property: JsonRequired] ImmutableArray<IntegrityScope> Scopes)
{
    public const int CurrentSchema = 1;

    public static IntegritySettingsSnapshot Create(IEnumerable<IntegrityScope> scopes, long generation)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var ordered = scopes.Take(IntegrityScope.MaximumScopes + 1).OrderBy(x => x?.Id).ToArray();
        if (!ValidScopes(ordered) || generation < 0 || generation == 0 && ordered.Length != 0) throw new ArgumentException("Invalid integrity scopes or generation.", nameof(scopes));
        return new(CurrentSchema, generation, ComputeRevision(ordered, generation), ImmutableArray.CreateRange(ordered));
    }

    public bool IsValid() => SchemaVersion == CurrentSchema && Generation >= 0 && !Scopes.IsDefault && ValidScopes(Scopes) &&
        (Generation != 0 || Scopes.IsEmpty) && string.Equals(Revision, ComputeRevision(Scopes.OrderBy(x => x.Id), Generation), StringComparison.Ordinal);

    /// <summary>Prevents an existing scope from rolling back or changing under the same revision.</summary>
    public bool CanFollow(IntegritySettingsSnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!IsValid() || !previous.IsValid()) return false;
        if (Generation < previous.Generation || Generation == previous.Generation && Revision != previous.Revision) return false;
        var prior = previous.Scopes.ToDictionary(x => x.Id);
        foreach (var scope in Scopes)
        {
            if (!prior.TryGetValue(scope.Id, out var old)) continue;
            if (scope.Revision < old.Revision || scope.Revision == old.Revision && !scope.SamePolicy(old)) return false;
        }
        return true;
    }

    private static bool ValidScopes(IReadOnlyCollection<IntegrityScope> scopes)
    {
        if (scopes.Count > IntegrityScope.MaximumScopes) return false;
        var ids = new HashSet<Guid>();
        foreach (var scope in scopes)
            if (scope is null || !scope.IsValid() || !ids.Add(scope.Id)) return false;
        return true;
    }

    private static string ComputeRevision(IEnumerable<IntegrityScope> scopes, long generation) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new RevisionInput(CurrentSchema, generation, scopes))));

    private sealed record RevisionInput(int SchemaVersion, long Generation, IEnumerable<IntegrityScope> Scopes);
}

/// <summary>Reads one device's policy within its already-authenticated Agent transaction.</summary>
public interface IIntegritySettingsReader
{
    Task<IntegritySettingsSnapshot> ReadAsync(DbConnection connection, DbTransaction transaction, Guid deviceId, CancellationToken token);
}
