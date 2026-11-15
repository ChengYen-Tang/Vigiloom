using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaCorrelationCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SigmaCorrelationCoverage",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivationVersion = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    CompleteSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IncompleteUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigmaCorrelationCoverage", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_SigmaCorrelationCoverage_SigmaRuleTests_TestId",
                        column: x => x.TestId,
                        principalSchema: "security",
                        principalTable: "SigmaRuleTests",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SigmaCorrelationCoverage_TestId_ActivationVersion",
                schema: "security",
                table: "SigmaCorrelationCoverage",
                columns: new[] { "TestId", "ActivationVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaCorrelationCoverage] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Sigma correlation coverage prevents downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "SigmaCorrelationCoverage",
                schema: "security");
        }
    }
}
