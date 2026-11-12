using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartialObservationChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Content",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Content",
                schema: "security",
                table: "InventoryChanges",
                sql: "([Kind] IN (1, 4) AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] IN (2, 5) AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges",
                sql: "[Generation] > 0 AND [Source] BETWEEN 1 AND 15 AND [Kind] BETWEEN 1 AND 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Content",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Content",
                schema: "security",
                table: "InventoryChanges",
                sql: "([Kind] = 1 AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 2 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryChanges_Values",
                schema: "security",
                table: "InventoryChanges",
                sql: "[Generation] > 0 AND [Source] BETWEEN 1 AND 15 AND [Kind] BETWEEN 1 AND 3");
        }
    }
}
