using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Publishing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TemplateHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HistoryDays",
                schema: "publishing",
                table: "Templates",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HistorySince",
                schema: "publishing",
                table: "Templates",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Templates_HistoryPeriod",
                schema: "publishing",
                table: "Templates",
                sql: "([HistoryDays] = 0 AND [HistorySince] IS NULL) OR ([HistoryDays] BETWEEN 1 AND 7 AND [HistorySince] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [publishing].[Templates] WITH (TABLOCKX, HOLDLOCK) WHERE [HistoryDays] <> 0 OR [HistorySince] IS NOT NULL) THROW 51008, 'Cannot discard configured template history.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Templates_HistoryPeriod",
                schema: "publishing",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "HistoryDays",
                schema: "publishing",
                table: "Templates");

            migrationBuilder.DropColumn(
                name: "HistorySince",
                schema: "publishing",
                table: "Templates");
        }
    }
}
