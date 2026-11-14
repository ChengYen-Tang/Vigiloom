using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KevNormalizedRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IntelligenceRevisions_SourceId_Digest",
                schema: "security",
                table: "IntelligenceRevisions");

            migrationBuilder.AddColumn<bool>(
                name: "Normalized",
                schema: "security",
                table: "IntelligenceRevisions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "KevRecords",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CveId = table.Column<string>(type: "varchar(28)", unicode: false, maxLength: 28, nullable: false),
                    VendorProject = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Product = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VulnerabilityName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DateAdded = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredAction = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RansomwareUse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ForensicTriage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CwesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KevRecords", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_KevRecords_IntelligenceRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceRevisions_SourceId_Digest_MappingVersion",
                schema: "security",
                table: "IntelligenceRevisions",
                columns: new[] { "SourceId", "Digest", "MappingVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KevRecords_RevisionId_CveId",
                schema: "security",
                table: "KevRecords",
                columns: new[] { "RevisionId", "CveId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[KevRecords] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[IntelligenceRevisions] WITH (TABLOCKX, HOLDLOCK)
                        GROUP BY [SourceId], [Digest] HAVING COUNT_BIG(*) > 1)
                    THROW 51000, 'Normalized intelligence records or mapping revisions prevent downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "KevRecords",
                schema: "security");

            migrationBuilder.DropIndex(
                name: "IX_IntelligenceRevisions_SourceId_Digest_MappingVersion",
                schema: "security",
                table: "IntelligenceRevisions");

            migrationBuilder.DropColumn(
                name: "Normalized",
                schema: "security",
                table: "IntelligenceRevisions");

            migrationBuilder.CreateIndex(
                name: "IX_IntelligenceRevisions_SourceId_Digest",
                schema: "security",
                table: "IntelligenceRevisions",
                columns: new[] { "SourceId", "Digest" },
                unique: true);
        }
    }
}
