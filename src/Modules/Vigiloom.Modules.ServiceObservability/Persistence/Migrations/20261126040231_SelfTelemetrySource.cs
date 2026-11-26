using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SelfTelemetrySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSelf",
                schema: "service",
                table: "Sources",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Sources_IsSelf",
                schema: "service",
                table: "Sources",
                column: "IsSelf",
                unique: true,
                filter: "[IsSelf] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sources_IsSelf",
                schema: "service",
                table: "Sources");

            migrationBuilder.DropColumn(
                name: "IsSelf",
                schema: "service",
                table: "Sources");
        }
    }
}
