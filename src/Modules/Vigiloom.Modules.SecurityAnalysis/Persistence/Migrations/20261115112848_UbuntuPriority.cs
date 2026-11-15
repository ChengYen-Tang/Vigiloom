using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UbuntuPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UbuntuPriority",
                schema: "security",
                table: "VulnerabilityEvaluations",
                type: "varchar(10)",
                unicode: false,
                maxLength: 10,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddCheckConstraint(
                name: "CK_VulnerabilityEvaluations_UbuntuPriority",
                schema: "security",
                table: "VulnerabilityEvaluations",
                sql: "[UbuntuPriority] IS NULL OR [UbuntuPriority] IN ('critical', 'high', 'medium', 'low', 'negligible')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep the same source-before-evaluation lock order as assessment publication and the preceding migration.
            migrationBuilder.Sql("DECLARE @sourceCount bigint; SELECT @sourceCount = COUNT_BIG(*) FROM [security].[IntelligenceSources] WITH (TABLOCKX, HOLDLOCK);");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [security].[VulnerabilityEvaluations] WITH (TABLOCKX, HOLDLOCK) WHERE [UbuntuPriority] IS NOT NULL) THROW 51000, 'Cannot remove retained Ubuntu priority.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_VulnerabilityEvaluations_UbuntuPriority",
                schema: "security",
                table: "VulnerabilityEvaluations");

            migrationBuilder.DropColumn(
                name: "UbuntuPriority",
                schema: "security",
                table: "VulnerabilityEvaluations");
        }
    }
}
