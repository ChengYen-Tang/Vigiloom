using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentPackageManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManifestSha256",
                schema: "agent",
                table: "Packages",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: false,
                defaultValue: "");
            migrationBuilder.Sql("UPDATE [agent].[Packages] SET [Listed] = 0 WHERE [Listed] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[Packages] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot discard package manifest bindings.', 1;");
            migrationBuilder.DropColumn(
                name: "ManifestSha256",
                schema: "agent",
                table: "Packages");
        }
    }
}
