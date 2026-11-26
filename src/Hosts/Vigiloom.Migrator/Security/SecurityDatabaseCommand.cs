using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Migrator.Security;

internal static class SecurityDatabaseCommand
{
    // Parents precede their dependent tables so the copy keeps foreign-key checks enabled.
    internal static readonly string[] Tables =
    [
        "IntelligenceSources", "IntelligenceRevisions", "UbuntuDocuments", "MsrcArtifacts",
        "InventorySources", "InventorySnapshots", "InventoryChunks", "InventoryChanges",
        "InventorySettings", "InventorySettingAudits", "IntegrityScopes", "IntegrityScopeAudits",
        "IntegrityPolicyStates", "IntegrityScopeStates", "IntegrityScans", "IntegrityChunks", "IntegrityChanges", "BaselineObservations",
        "IntelligenceActivations", "IntelligenceCaches", "IntelligenceActivationEvents", "KevRecords", "DebianAdvisories",
        "UbuntuScopes", "UbuntuRevisionDocuments", "AdvisoryImports", "AdvisoryImportPages", "AdvisoryImportEntries",
        "AdvisoryRevisionEntries", "SigmaRules", "SigmaManifests", "SignatureFiles", "IocEntries", "MsrcDocuments",
        "MsrcCveIndexes", "MsrcProductIndexes", "EpssScores", "OfflineImports", "IocDetections",
        "SigmaRuleTests", "SigmaRuleActivations", "SigmaDetectionResults", "SigmaCorrelationEvents", "SigmaCorrelationCoverage", "SigmaEventGaps",
        "VulnerabilityFindings", "VulnerabilityEvaluations", "VulnerabilityCoverage", "VulnerabilityRuns", "VulnerabilityInventoryEvidence", "VulnerabilityUnknownPackages"
    ];

    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_SECURITY_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_SECURITY_SQL_CONNECTION to an existing security database.");
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        await RunAsync(target, source, token);
    }

    internal static async Task RunAsync(string target, string? source, CancellationToken token)
    {
        SqlTableDataMigration.ValidateTarget(target);
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(target,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using (var db = new SecurityAnalysisDbContext(options)) await db.Database.MigrateAsync(token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(target,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        if (source is not null)
        {
            var mappings = Tables.Select(name => new SqlTableDataMigration.TableMapping("security", name, "security", name)).ToList();
            mappings.Add(new("agent", "YaraScans", "security", "YaraScans"));
            await SqlTableDataMigration.CopyAsync(source, target, mappings, token);
        }
        Console.WriteLine(source is null ? "Security schema initialized successfully." : "Security data copied successfully.");
    }
}
