using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OfflineImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OfflineImports",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OfflineImports", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_OfflineImports_Actor", "[ActorId] <> '00000000-0000-0000-0000-000000000000'");
                    table.ForeignKey(
                        name: "FK_OfflineImports_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_OfflineImports_SourceId_RevisionId_ImportedAt",
                schema: "security",
                table: "OfflineImports",
                columns: new[] { "SourceId", "RevisionId", "ImportedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [security].[OfflineImports] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove retained offline import history.', 1;");
            migrationBuilder.DropTable(
                name: "OfflineImports",
                schema: "security");
        }
    }
}
