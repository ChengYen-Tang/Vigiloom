using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricNameLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricContexts_ServiceId",
                schema: "service",
                table: "MetricContexts");

            migrationBuilder.AddColumn<string>(
                name: "MetricName",
                schema: "service",
                table: "MetricContexts",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "MetricNameBytes",
                schema: "service",
                table: "MetricContexts",
                type: "varbinary(512)",
                nullable: true,
                computedColumnSql: "CONVERT(varbinary(512), [MetricName])",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ContextId_ReceivedAt_RowId",
                schema: "service",
                table: "MetricPoints",
                columns: new[] { "ContextId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricContexts_ServiceId_MetricNameBytes",
                schema: "service",
                table: "MetricContexts",
                columns: new[] { "ServiceId", "MetricNameBytes" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MetricPoints_ContextId_ReceivedAt_RowId",
                schema: "service",
                table: "MetricPoints");

            migrationBuilder.DropIndex(
                name: "IX_MetricContexts_ServiceId_MetricNameBytes",
                schema: "service",
                table: "MetricContexts");

            migrationBuilder.DropColumn(
                name: "MetricNameBytes",
                schema: "service",
                table: "MetricContexts");

            migrationBuilder.DropColumn(
                name: "MetricName",
                schema: "service",
                table: "MetricContexts");

            migrationBuilder.CreateIndex(
                name: "IX_MetricContexts_ServiceId",
                schema: "service",
                table: "MetricContexts",
                column: "ServiceId");
        }
    }
}
