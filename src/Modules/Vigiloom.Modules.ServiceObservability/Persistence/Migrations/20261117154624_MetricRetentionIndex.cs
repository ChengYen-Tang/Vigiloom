using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MetricRollupWindows_WindowStart",
                schema: "service",
                table: "MetricRollupWindows",
                column: "WindowStart")
                .Annotation("SqlServer:Include", new[] { "ServiceId", "RawPruned" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricRollupWindows_WindowStart",
                schema: "service",
                table: "MetricRollupWindows");
        }
    }
}
