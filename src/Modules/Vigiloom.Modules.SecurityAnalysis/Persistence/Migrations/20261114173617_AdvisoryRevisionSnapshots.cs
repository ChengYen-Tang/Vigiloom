using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvisoryRevisionSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ReleasedAt",
                schema: "security",
                table: "IntelligenceRevisions",
                type: "datetimeoffset",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset");

            migrationBuilder.AddColumn<Guid>(
                name: "ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AdvisoryImportEntries_RowId_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries",
                columns: new[] { "RowId", "AdvisoryId" })
                .Annotation("SqlServer:Clustered", false);

            migrationBuilder.CreateTable(
                name: "AdvisoryRevisionEntries",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryRowId = table.Column<long>(type: "bigint", nullable: false),
                    AdvisoryId = table.Column<string>(type: "varchar(19)", unicode: false, maxLength: 19, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdvisoryRevisionEntries", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_AdvisoryRevisionEntries_AdvisoryImportEntries_EntryRowId_AdvisoryId",
                        columns: x => new { x.EntryRowId, x.AdvisoryId },
                        principalSchema: "security",
                        principalTable: "AdvisoryImportEntries",
                        principalColumns: new[] { "RowId", "AdvisoryId" });
                    table.ForeignKey(
                        name: "FK_AdvisoryRevisionEntries_IntelligenceRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryImports_SourceId_ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports",
                columns: new[] { "SourceId", "ResultRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRevisionEntries_EntryRowId_AdvisoryId",
                schema: "security",
                table: "AdvisoryRevisionEntries",
                columns: new[] { "EntryRowId", "AdvisoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_AdvisoryRevisionEntries_RevisionId_AdvisoryId",
                schema: "security",
                table: "AdvisoryRevisionEntries",
                columns: new[] { "RevisionId", "AdvisoryId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AdvisoryImports_IntelligenceRevisions_SourceId_ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports",
                columns: new[] { "SourceId", "ResultRevisionId" },
                principalSchema: "security",
                principalTable: "IntelligenceRevisions",
                principalColumns: new[] { "SourceId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[AdvisoryImports] WITH (TABLOCKX, HOLDLOCK) WHERE [ResultRevisionId] IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM [security].[AdvisoryRevisionEntries] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[IntelligenceRevisions] WITH (TABLOCKX, HOLDLOCK) WHERE [ReleasedAt] IS NULL)
                    THROW 51000, 'Advisory revision history prevents downgrade.', 1;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AdvisoryImports_IntelligenceRevisions_SourceId_ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropTable(
                name: "AdvisoryRevisionEntries",
                schema: "security");

            migrationBuilder.DropIndex(
                name: "IX_AdvisoryImports_SourceId_ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AdvisoryImportEntries_RowId_AdvisoryId",
                schema: "security",
                table: "AdvisoryImportEntries");

            migrationBuilder.DropColumn(
                name: "ResultRevisionId",
                schema: "security",
                table: "AdvisoryImports");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ReleasedAt",
                schema: "security",
                table: "IntelligenceRevisions",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "datetimeoffset",
                oldNullable: true);
        }
    }
}
