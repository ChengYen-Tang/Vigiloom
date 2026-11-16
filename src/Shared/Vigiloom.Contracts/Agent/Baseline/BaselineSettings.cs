using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Contracts.Agent.Baseline;

/// <summary>Identifies the published source revision used for one reviewed check.</summary>
public sealed record BaselineSelection(Guid SourceId, Guid RevisionId, string ActivationVersion, BaselineCheck Check)
{
    public static bool IsActivationVersionValid(string? value)
    {
        Span<byte> bytes = stackalloc byte[8];
        return value is { Length: <= 64 } && Convert.TryFromBase64String(value, bytes, out var written) && written == bytes.Length;
    }
}

/// <summary>Versioned policy sent to an authenticated Agent.</summary>
public sealed record BaselineSettingsSnapshot(int SchemaVersion, string Revision, int ScanIntervalSeconds,
    [property: JsonRequired] IReadOnlyList<BaselineSelection> Checks)
{
    public const int CurrentSchema = 1;
    public const int DefaultIntervalSeconds = 21600;

    public static BaselineSettingsSnapshot Create(int intervalSeconds, IReadOnlyList<BaselineSelection> checks)
    {
        var sorted = checks.OrderBy(x => x.SourceId).ThenBy(x => x.Check.Id, StringComparer.Ordinal).ToArray();
        var snapshot = new BaselineSettingsSnapshot(CurrentSchema, string.Empty, intervalSeconds, sorted);
        if (!snapshot.HasValidContents()) throw new ArgumentException("Invalid baseline settings.", nameof(checks));
        return snapshot with { Revision = RevisionFor(intervalSeconds, sorted) };
    }

    public bool IsValid() => HasValidContents() && string.Equals(Revision, RevisionFor(ScanIntervalSeconds, Checks), StringComparison.Ordinal);

    private bool HasValidContents()
    {
        if (SchemaVersion != CurrentSchema || ScanIntervalSeconds is < 900 or > 604800 || Checks is not { Count: <= 100 }) return false;
        var seen = new HashSet<(Guid, string)>();
        foreach (var selection in Checks)
        {
            if (selection is null || selection.SourceId.Version != 7 || selection.RevisionId.Version != 7 ||
                !BaselineSelection.IsActivationVersionValid(selection.ActivationVersion) || selection.Check is not { } check || !Text(check.Id, 128) ||
                !Text(check.Title, 256) || !Text(check.Platform, 32) || !Text(check.Operation, 128) ||
                !Text(check.Target, 1024) || !Text(check.Expected, 256) ||
                (!Text(check.SourceUrl, 2048) && !(check.SourceUrl == "" && CustomBaselineCheck.IsValid(check))) ||
                !Text(check.License, 256) || check.Versions is not { Length: > 0 and <= 32 } ||
                check.Guidance is { } guidance && (guidance.Length > 1024 || guidance.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) ||
                check.Versions.Any(x => !Text(x, 128)) || !seen.Add((selection.SourceId, check.Id))) return false;
        }
        return true;
    }

    private static string RevisionFor(int interval, IReadOnlyList<BaselineSelection> checks)
    {
        var ordered = checks.OrderBy(x => x.SourceId).ThenBy(x => x.Check.Id, StringComparer.Ordinal);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = CurrentSchema, ScanIntervalSeconds = interval, Checks = ordered });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool Text(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
}
