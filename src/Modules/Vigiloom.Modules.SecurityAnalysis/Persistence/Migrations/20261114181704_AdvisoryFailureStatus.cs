using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryFailureStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureCode",
                schema: "security",
                table: "AdvisoryImports",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[FailureCode] IS NULL OR [FailureCode] IN ('fetch-failed','invalid-response','request-timeout','cursor-rejected','limit-exceeded','invalid-base')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [FailureCode] IS NOT NULL)
                    THROW 51000, 'Advisory failure status prevents downgrade.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_FailureCode",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "FailureCode",
                schema: "security",
                table: "AdvisoryImports");
        }
    }
}
