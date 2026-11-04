using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryReceptionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_ServiceId_ReceivedAt",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "ServiceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ServiceId_ReceivedAt",
                schema: "service",
                table: "MetricPoints",
                columns: new[] { "ServiceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_ReceivedAt",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "ServiceId", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TraceSpans_ServiceId_ReceivedAt",
                schema: "service",
                table: "TraceSpans");

            migrationBuilder.DropIndex(
                name: "IX_MetricPoints_ServiceId_ReceivedAt",
                schema: "service",
                table: "MetricPoints");

            migrationBuilder.DropIndex(
                name: "IX_LogRecords_ServiceId_ReceivedAt",
                schema: "service",
                table: "LogRecords");
        }
    }
}
