using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedCredential",
                schema: "security",
                table: "IntelligenceSources",
                type: "nvarchar(max)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[FailureCode] IS NULL OR [FailureCode] IN ('fetch-failed','invalid-response','request-timeout','cursor-rejected','limit-exceeded','invalid-base','credential-unavailable')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntelligenceSources] WITH (TABLOCKX, HOLDLOCK) WHERE [ProtectedCredential] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [FailureCode] = 'credential-unavailable')
                    THROW 51000, 'Advisory credential configuration prevents downgrade.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "ProtectedCredential",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[FailureCode] IS NULL OR [FailureCode] IN ('fetch-failed','invalid-response','request-timeout','cursor-rejected','limit-exceeded','invalid-base')");
        }
    }
}
