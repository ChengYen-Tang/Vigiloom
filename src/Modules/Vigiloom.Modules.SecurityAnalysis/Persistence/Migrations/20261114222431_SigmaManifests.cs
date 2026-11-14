using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaManifests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExclusionReason",
                schema: "security",
                table: "SigmaRules",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SigmaManifests",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    IncludedCount = table.Column<int>(type: "int", nullable: false),
                    ExcludedCount = table.Column<int>(type: "int", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaManifests", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SigmaManifests_Count", "[IncludedCount] >= 0 AND [ExcludedCount] >= 0");
                    table.ForeignKey(
                        name: "FK_SigmaManifests_IntelligenceRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaManifests_RevisionId",
                schema: "security",
                table: "SigmaManifests",
                column: "RevisionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaManifests] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot remove retained Sigma manifests.', 1;
                """);
            migrationBuilder.DropTable(
                name: "SigmaManifests",
                schema: "security");

            migrationBuilder.DropColumn(
                name: "ExclusionReason",
                schema: "security",
                table: "SigmaRules");
        }
    }
}
