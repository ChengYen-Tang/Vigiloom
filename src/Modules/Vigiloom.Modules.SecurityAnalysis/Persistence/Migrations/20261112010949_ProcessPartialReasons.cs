using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessPartialReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PartialReason",
                schema: "security",
                table: "InventorySnapshots",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventorySnapshots] WITH (TABLOCKX, HOLDLOCK) WHERE [PartialReason] IS NOT NULL)
                    THROW 51000, 'Cannot downgrade while partial observation reasons exist.', 1;
                """);
            migrationBuilder.DropColumn(
                name: "PartialReason",
                schema: "security",
                table: "InventorySnapshots");
        }
    }
}
