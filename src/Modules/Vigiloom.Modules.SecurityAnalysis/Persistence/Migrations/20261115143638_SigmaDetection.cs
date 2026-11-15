using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigmaRuleTests",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Digest = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    ActivationVersion = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                    MatcherVersion = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SamplesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Passed = table.Column<bool>(type: "bit", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaRuleTests", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.UniqueConstraint("AK_SigmaRuleTests_Id_SourceId", x => new { x.Id, x.SourceId })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SigmaRuleTests_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                });

            migrationBuilder.CreateTable(
                name: "SigmaRuleActivations",
                schema: "security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaRuleActivations", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_SigmaRuleActivations_SigmaRuleTests_TestId_SourceId",
                        columns: x => new { x.TestId, x.SourceId },
                        principalSchema: "security",
                        principalTable: "SigmaRuleTests",
                        principalColumns: new[] { "Id", "SourceId" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleActivations_RowId",
                schema: "security",
                table: "SigmaRuleActivations",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleActivations_SourceId_Path",
                schema: "security",
                table: "SigmaRuleActivations",
                columns: new[] { "SourceId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleActivations_TestId_SourceId",
                schema: "security",
                table: "SigmaRuleActivations",
                columns: new[] { "TestId", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleTests_RowId",
                schema: "security",
                table: "SigmaRuleTests",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleTests_SourceId_Path_TestedAt_RowId",
                schema: "security",
                table: "SigmaRuleTests",
                columns: new[] { "SourceId", "Path", "TestedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRuleTests_SourceId_RevisionId",
                schema: "security",
                table: "SigmaRuleTests",
                columns: new[] { "SourceId", "RevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaRuleActivations] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[SigmaRuleTests] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Sigma detection data prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "SigmaRuleActivations",
                schema: "security");

            migrationBuilder.DropTable(
                name: "SigmaRuleTests",
                schema: "security");
        }
    }
}
