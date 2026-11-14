using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntelligenceSourceLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources",
                sql: "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 1073741824 AND [MaximumRecords] BETWEEN 1 AND 1000000 AND ([Key] = 'github-reviewed' OR [MaximumBytes] <= 67108864)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntelligenceSources] WITH (TABLOCKX, HOLDLOCK) WHERE [MaximumBytes] > 67108864)
                    THROW 51000, 'Intelligence source limits prevent downgrade.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources",
                sql: "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 67108864 AND [MaximumRecords] BETWEEN 1 AND 1000000");
        }
    }
}
