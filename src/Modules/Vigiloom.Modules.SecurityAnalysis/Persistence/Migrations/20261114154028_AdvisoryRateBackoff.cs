using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryRateBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateLimitAttempts",
                schema: "security",
                table: "AdvisoryImports",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_RateLimitAttempts",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[RateLimitAttempts] BETWEEN 0 AND 20");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [RateLimitAttempts] > 0)
                    THROW 51000, 'Advisory rate limit backoff prevents downgrade.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_RateLimitAttempts",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "RateLimitAttempts",
                schema: "security",
                table: "AdvisoryImports");
        }
    }
}
