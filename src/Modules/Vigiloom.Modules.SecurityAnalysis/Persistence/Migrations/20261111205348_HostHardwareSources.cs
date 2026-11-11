using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostHardwareSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InventorySources_Source",
                schema: "security",
                table: "InventorySources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventorySources_Source",
                schema: "security",
                table: "InventorySources",
                sql: "[Source] BETWEEN 1 AND 12");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges",
                sql: "[Generation] > 0 AND [Source] BETWEEN 1 AND 12 AND [Kind] BETWEEN 1 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InventorySources_Source",
                schema: "security",
                table: "InventorySources");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventorySources_Source",
                schema: "security",
                table: "InventorySources",
                sql: "[Source] BETWEEN 1 AND 10");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges",
                sql: "[Generation] > 0 AND [Source] BETWEEN 1 AND 10 AND [Kind] BETWEEN 1 AND 3");
        }
    }
}
