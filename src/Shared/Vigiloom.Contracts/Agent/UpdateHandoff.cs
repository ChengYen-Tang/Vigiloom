using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>One local update request; the credential must remain in memory only.</summary>
public sealed class UpdateHandoff
{
    public const string WindowsPipeName = "Vigiloom.Updater";
    [JsonRequired] public Guid UpdateId { get; init; }
    [JsonRequired] public string Credential { get; init; } = "";

    public bool IsValid()
    {
        if (UpdateId.Version != 7 || Credential is not { Length: 84 } ||
            !Credential.StartsWith("vla_", StringComparison.Ordinal) || Credential[40] != '.' ||
            !Guid.TryParseExact(Credential.AsSpan(4, 36), "D", out var id) || id.Version != 7) return false;
        foreach (var character in Credential.AsSpan(41))
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    public override string ToString() => $"UpdateHandoff {{ UpdateId = {UpdateId:D}, Credential = [redacted] }}";
}

public enum UpdateHandoffStatus { Accepted = 1, Busy = 2, Rejected = 3 }

/// <summary>Acceptance of a request is not evidence that the update succeeded.</summary>
public sealed class UpdateHandoffResponse
{
    [JsonRequired] public UpdateHandoffStatus Status { get; init; }
    public bool IsValid() => Status is UpdateHandoffStatus.Accepted or UpdateHandoffStatus.Busy or UpdateHandoffStatus.Rejected;
}
