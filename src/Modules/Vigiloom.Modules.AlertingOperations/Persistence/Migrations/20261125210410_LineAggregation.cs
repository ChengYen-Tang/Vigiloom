using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LineAggregation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AggregationSeconds",
                schema: "operations",
                table: "LineSettings",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AggregationStartedAt",
                schema: "operations",
                table: "LineSettings",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AggregationSeconds",
                schema: "operations",
                table: "LineSettings");

            migrationBuilder.DropColumn(
                name: "AggregationStartedAt",
                schema: "operations",
                table: "LineSettings");
        }
    }
}
