using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricWindowBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WindowsTracked",
                schema: "service",
                table: "MetricContexts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_MetricContexts_UntrackedWindows",
                schema: "service",
                table: "MetricContexts",
                column: "RowId",
                filter: "[WindowsTracked] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricContexts_UntrackedWindows",
                schema: "service",
                table: "MetricContexts");

            migrationBuilder.DropColumn(
                name: "WindowsTracked",
                schema: "service",
                table: "MetricContexts");
        }
    }
}
