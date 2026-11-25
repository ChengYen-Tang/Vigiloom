using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Persistence;

/// <summary>Copies existing Identity data into an empty, EF-migrated Identity database.</summary>
public static class IdentityDataMigration
{
    private static readonly string[] Tables =
    [
        "AspNetRoles", "AspNetUsers", "AspNetRoleClaims", "AspNetUserClaims", "AspNetUserLogins",
        "AspNetUserRoles", "AspNetUserTokens", "AccountAuditEvents", "ApiCredentials", "ApiCredentialAuditEvents"
    ];

    public static void ValidateConnections(string sourceConnection, string targetConnection) =>
        SqlTableDataMigration.ValidateConnections(sourceConnection, targetConnection);

    public static void ValidateTarget(string targetConnection) => SqlTableDataMigration.ValidateTarget(targetConnection);

    public static Task CopyAsync(string sourceConnection, string targetConnection, CancellationToken token = default) =>
        SqlTableDataMigration.CopyAsync(sourceConnection, targetConnection, "identity", Tables, token);
}
