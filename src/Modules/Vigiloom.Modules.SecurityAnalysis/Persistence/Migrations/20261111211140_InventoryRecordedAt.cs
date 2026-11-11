using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryRecordedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecordedAt",
                schema: "security",
                table: "InventoryChanges",
                type: "datetimeoffset",
                nullable: false,
                defaultValueSql: "TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryChanges_RecordedAt_RowId",
                schema: "security",
                table: "InventoryChanges",
                columns: new[] { "RecordedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventoryChanges] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Inventory change publication times prevent downgrade.', 1;
                """);
            migrationBuilder.DropIndex(
                name: "IX_InventoryChanges_RecordedAt_RowId",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.DropColumn(
                name: "RecordedAt",
                schema: "security",
                table: "InventoryChanges");
        }
    }
}
