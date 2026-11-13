using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResourcePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                schema: "resources",
                table: "Groups",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicDisplayName",
                schema: "resources",
                table: "Groups",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicPolicyVersion",
                schema: "resources",
                table: "Groups",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublic",
                schema: "resources",
                table: "Devices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicDisplayName",
                schema: "resources",
                table: "Devices",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicPolicyVersion",
                schema: "resources",
                table: "Devices",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Groups_IsPublic_PublicDisplayName_RowId",
                schema: "resources",
                table: "Groups",
                columns: new[] { "IsPublic", "PublicDisplayName", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IsPublic_PublicDisplayName_RowId",
                schema: "resources",
                table: "Devices",
                columns: new[] { "IsPublic", "PublicDisplayName", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve configured publication choices; locks remain held for the migration transaction.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [resources].[Devices] WITH (TABLOCKX, HOLDLOCK) WHERE [IsPublic] = 1 OR [PublicDisplayName] IS NOT NULL OR [PublicPolicyVersion] IS NOT NULL) THROW 51001, 'Cannot remove configured device publication settings.', 1;");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [resources].[Groups] WITH (TABLOCKX, HOLDLOCK) WHERE [IsPublic] = 1 OR [PublicDisplayName] IS NOT NULL OR [PublicPolicyVersion] IS NOT NULL) THROW 51002, 'Cannot remove configured group publication settings.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_Groups_IsPublic_PublicDisplayName_RowId",
                schema: "resources",
                table: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_Devices_IsPublic_PublicDisplayName_RowId",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                schema: "resources",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PublicDisplayName",
                schema: "resources",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PublicPolicyVersion",
                schema: "resources",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "IsPublic",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PublicDisplayName",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PublicPolicyVersion",
                schema: "resources",
                table: "Devices");
        }
    }
}
