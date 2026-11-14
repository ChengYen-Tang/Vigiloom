using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntelligenceSourcePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaximumBytes",
                schema: "security",
                table: "IntelligenceSources",
                type: "int",
                nullable: false,
                defaultValue: 16777216);

            migrationBuilder.AddColumn<int>(
                name: "MaximumRecords",
                schema: "security",
                table: "IntelligenceSources",
                type: "int",
                nullable: false,
                defaultValue: 50000);

            migrationBuilder.AddColumn<int>(
                name: "StaleAfterSeconds",
                schema: "security",
                table: "IntelligenceSources",
                type: "int",
                nullable: false,
                defaultValue: 259200);

            migrationBuilder.AddColumn<int>(
                name: "UpdateIntervalSeconds",
                schema: "security",
                table: "IntelligenceSources",
                type: "int",
                nullable: false,
                defaultValue: 86400);

            migrationBuilder.AddCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources",
                sql: "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 67108864 AND [MaximumRecords] BETWEEN 1 AND 1000000");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntelligenceSources] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Intelligence source policy prevents downgrade.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_IntelligenceSources_Settings",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.DropColumn(
                name: "MaximumBytes",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.DropColumn(
                name: "MaximumRecords",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.DropColumn(
                name: "StaleAfterSeconds",
                schema: "security",
                table: "IntelligenceSources");

            migrationBuilder.DropColumn(
                name: "UpdateIntervalSeconds",
                schema: "security",
                table: "IntelligenceSources");
        }
    }
}
