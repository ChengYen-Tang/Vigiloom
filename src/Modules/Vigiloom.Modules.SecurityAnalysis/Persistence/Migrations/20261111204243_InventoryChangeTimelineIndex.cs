using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryChangeTimelineIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InventoryChanges_DeviceId_ObservedAt_RowId",
                schema: "security",
                table: "InventoryChanges",
                columns: new[] { "DeviceId", "ObservedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryChanges_DeviceId_ObservedAt_RowId",
                schema: "security",
                table: "InventoryChanges");
        }
    }
}
