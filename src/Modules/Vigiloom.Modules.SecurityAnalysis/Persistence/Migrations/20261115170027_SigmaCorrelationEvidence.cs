using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SigmaCorrelationEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CorrelationCount",
                schema: "security",
                table: "SigmaDetectionResults",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CorrelationFirstAt",
                schema: "security",
                table: "SigmaDetectionResults",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CorrelationLastAt",
                schema: "security",
                table: "SigmaDetectionResults",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SigmaDetectionResults_Correlation",
                schema: "security",
                table: "SigmaDetectionResults",
                sql: "([CorrelationCount] IS NULL AND [CorrelationFirstAt] IS NULL AND [CorrelationLastAt] IS NULL) OR ([Result] = 1 AND [CorrelationCount] IS NOT NULL AND [CorrelationCount] > 0 AND [CorrelationFirstAt] IS NOT NULL AND [CorrelationLastAt] IS NOT NULL AND [CorrelationFirstAt] <= [CorrelationLastAt])");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[SigmaDetectionResults] WITH (TABLOCKX, HOLDLOCK)
                    WHERE [CorrelationCount] IS NOT NULL OR [CorrelationFirstAt] IS NOT NULL OR [CorrelationLastAt] IS NOT NULL)
                    THROW 51000, 'Sigma correlation evidence prevents downgrade.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_SigmaDetectionResults_Correlation",
                schema: "security",
                table: "SigmaDetectionResults");

            migrationBuilder.DropColumn(
                name: "CorrelationCount",
                schema: "security",
                table: "SigmaDetectionResults");

            migrationBuilder.DropColumn(
                name: "CorrelationFirstAt",
                schema: "security",
                table: "SigmaDetectionResults");

            migrationBuilder.DropColumn(
                name: "CorrelationLastAt",
                schema: "security",
                table: "SigmaDetectionResults");
        }
    }
}
