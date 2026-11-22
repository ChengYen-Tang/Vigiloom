using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>One local update request carrying the Agent credential to the protected Updater service.</summary>
public sealed class UpdateHandoff
{
    public const string WindowsPipeName = "Vigiloom.Updater";
    [JsonRequired] public Guid UpdateId { get; init; }
    [JsonRequired] public string Credential { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool SyncOnly { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool StageCredential { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? PendingCredential { get; init; }

    public bool IsValid()
    {
        if (!TryGetAgentId(Credential, out var agentId)) return false;
        if (StageCredential)
            return SyncOnly && UpdateId == Guid.Empty && TryGetAgentId(PendingCredential, out var pendingId) &&
                pendingId == agentId && Credential != PendingCredential;
        return PendingCredential is null && (SyncOnly ? UpdateId == Guid.Empty : UpdateId.Version == 7);
    }

    public static bool TryGetAgentId(string? credential, out Guid agentId)
    {
        agentId = default;
        if (credential is not { Length: 84 } || !credential.StartsWith("vla_", StringComparison.Ordinal) || credential[40] != '.' ||
            !Guid.TryParseExact(credential.AsSpan(4, 36), "D", out var id) || id.Version != 7) return false;
        foreach (var character in credential.AsSpan(41))
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        agentId = id;
        return true;
    }

    public override string ToString() => $"UpdateHandoff {{ UpdateId = {UpdateId:D}, SyncOnly = {SyncOnly}, Credential = [redacted] }}";
}

public enum UpdateHandoffStatus { Accepted = 1, Busy = 2, Rejected = 3, CredentialStaged = 4 }

/// <summary>Acceptance of a request is not evidence that the update succeeded.</summary>
public sealed class UpdateHandoffResponse
{
    [JsonRequired] public UpdateHandoffStatus Status { get; init; }
    public bool IsValid() => Status is UpdateHandoffStatus.Accepted or UpdateHandoffStatus.Busy or UpdateHandoffStatus.Rejected or UpdateHandoffStatus.CredentialStaged;
}
