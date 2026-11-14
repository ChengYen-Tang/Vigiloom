using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImportPages_ImportId_Phase_NextCursor",
                schema: "security",
                table: "AdvisoryImportPages");

            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImportEntries_ImportId_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries");

            migrationBuilder.AddColumn<int>(
                name: "ReconciliationPhase",
                schema: "security",
                table: "AdvisoryImports",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ReconciliationRecordCount",
                schema: "security",
                table: "AdvisoryImports",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "Reconciliation",
                schema: "security",
                table: "AdvisoryImportPages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Reconciliation",
                schema: "security",
                table: "AdvisoryImportEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdvisoryImports_Reconciliation",
                schema: "security",
                table: "AdvisoryImports",
                sql: "[ReconciliationPhase] BETWEEN 1 AND 4 AND [ReconciliationRecordCount] BETWEEN 0 AND 100000");

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportPages_ImportId_Reconciliation_Phase_NextCursor",
                schema: "security",
                table: "AdvisoryImportPages",
                columns: new[] { "ImportId", "Reconciliation", "Phase", "NextCursor" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportEntries_ImportId_Reconciliation_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries",
                columns: new[] { "ImportId", "Reconciliation", "AdvisoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [ReconciliationPhase] <> 1 OR [ReconciliationRecordCount] <> 0)
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryImportPages] WITH (TABLOCKX, HOLDLOCK) WHERE [Reconciliation] = 1)
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryImportEntries] WITH (TABLOCKX, HOLDLOCK) WHERE [Reconciliation] = 1)
                    THROW 51000, 'Advisory reconciliation history prevents downgrade.', 1;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_AdvisoryImports_Reconciliation",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImportPages_ImportId_Reconciliation_Phase_NextCursor",
                schema: "security",
                table: "AdvisoryImportPages");

            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImportEntries_ImportId_Reconciliation_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries");

            migrationBuilder.DropColumn(
                name: "ReconciliationPhase",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "ReconciliationRecordCount",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropColumn(
                name: "Reconciliation",
                schema: "security",
                table: "AdvisoryImportPages");

            migrationBuilder.DropColumn(
                name: "Reconciliation",
                schema: "security",
                table: "AdvisoryImportEntries");

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportPages_ImportId_Phase_NextCursor",
                schema: "security",
                table: "AdvisoryImportPages",
                columns: new[] { "ImportId", "Phase", "NextCursor" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImportEntries_ImportId_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries",
                columns: new[] { "ImportId", "AdvisoryId" },
                unique: true);
        }
    }
}
