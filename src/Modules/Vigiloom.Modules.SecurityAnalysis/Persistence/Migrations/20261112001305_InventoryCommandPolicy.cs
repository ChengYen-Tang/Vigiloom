using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventoryCommandPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CollectCommandLine",
                schema: "security",
                table: "InventorySettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CollectCommandLine",
                schema: "security",
                table: "InventorySettingAudits",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EffectiveCollectCommandLine",
                schema: "security",
                table: "InventorySettingAudits",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventorySettings] WITH (TABLOCKX, HOLDLOCK) WHERE [CollectCommandLine] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [security].[InventorySettingAudits] WITH (TABLOCKX, HOLDLOCK)
                       WHERE [CollectCommandLine] IS NOT NULL OR [EffectiveCollectCommandLine] = 1)
                    THROW 51000, 'Cannot downgrade while command collection settings or audit history exist.', 1;
                """);
            migrationBuilder.DropColumn(
                name: "CollectCommandLine",
                schema: "security",
                table: "InventorySettings");

            migrationBuilder.DropColumn(
                name: "CollectCommandLine",
                schema: "security",
                table: "InventorySettingAudits");

            migrationBuilder.DropColumn(
                name: "EffectiveCollectCommandLine",
                schema: "security",
                table: "InventorySettingAudits");
        }
    }
}
