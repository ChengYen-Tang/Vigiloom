using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryCandidateSourceVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "ResultSourceVersion",
                schema: "security",
                table: "AdvisoryImports",
                type: "binary(8)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_SourceId_NotBefore",
                schema: "security",
                table: "AdvisoryImports",
                columns: new[] { "SourceId", "NotBefore" },
                filter: "[NotBefore] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [ResultSourceVersion] IS NOT NULL)
                    THROW 51000, 'Advisory candidate policy history prevents downgrade.', 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImports_SourceId_NotBefore",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "ResultSourceVersion",
                schema: "security",
                table: "AdvisoryImports");
        }
    }
}
