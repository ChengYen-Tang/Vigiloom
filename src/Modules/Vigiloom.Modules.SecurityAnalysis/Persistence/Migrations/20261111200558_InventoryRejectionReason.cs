using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryRejectionReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastRejectionReason",
                schema: "security",
                table: "InventorySources",
                type: "int",
                nullable: true);

            // An older rejected outcome did not record a reason; do not invent one.
            migrationBuilder.Sql("UPDATE [security].[InventorySources] SET [LastRejectionReason] = 3 WHERE [LastStatus] = 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventorySources_RejectionReason",
                schema: "security",
                table: "InventorySources",
                sql: "([LastStatus] IS NOT NULL AND [LastStatus] = 5 AND [LastRejectionReason] IS NOT NULL AND [LastRejectionReason] BETWEEN 1 AND 3) OR (([LastStatus] IS NULL OR [LastStatus] <> 5) AND [LastRejectionReason] IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventorySources] WITH (TABLOCKX, HOLDLOCK) WHERE [LastRejectionReason] IS NOT NULL)
                    THROW 51000, 'Inventory rejection reasons prevent downgrade.', 1;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_InventorySources_RejectionReason",
                schema: "security",
                table: "InventorySources");

            migrationBuilder.DropColumn(
                name: "LastRejectionReason",
                schema: "security",
                table: "InventorySources");
        }
    }
}
