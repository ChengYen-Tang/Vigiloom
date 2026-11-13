using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServicePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicDisplayName",
                schema: "service",
                table: "Services",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicPolicyVersion",
                schema: "service",
                table: "Services",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Services_IsPublic_PublicDisplayName_RowId",
                schema: "service",
                table: "Services",
                columns: new[] { "IsPublic", "PublicDisplayName", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A downgrade must not discard a configured publication choice or leave it detached.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [service].[Services] WITH (TABLOCKX, HOLDLOCK) WHERE [IsPublic] = 1 OR [PublicDisplayName] IS NOT NULL OR [PublicPolicyVersion] IS NOT NULL) THROW 51003, 'Cannot remove configured service publication settings.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_Services_IsPublic_PublicDisplayName_RowId",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "PublicDisplayName",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "PublicPolicyVersion",
                schema: "service",
                table: "Services");
        }
    }
}
