using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigmaRules",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    RuleId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Author = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    License = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Product = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Service = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaRules", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_SigmaRules_Content", "DATALENGTH([Content]) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_SigmaRules_IntelligenceRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRules_RevisionId_Path",
                schema: "security",
                table: "SigmaRules",
                columns: new[] { "RevisionId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigmaRules_RevisionId_RuleId",
                schema: "security",
                table: "SigmaRules",
                columns: new[] { "RevisionId", "RuleId" },
                unique: true,
                filter: "[RuleId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaRules] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Sigma rule data prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "SigmaRules",
                schema: "security");
        }
    }
}
