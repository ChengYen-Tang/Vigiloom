using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PingRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Aggregated",
                schema: "network",
                table: "PingSamples",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PingRollups",
                schema: "network",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    WindowStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<long>(type: "bigint", nullable: false),
                    Successes = table.Column<long>(type: "bigint", nullable: false),
                    Timeouts = table.Column<long>(type: "bigint", nullable: false),
                    Unreachable = table.Column<long>(type: "bigint", nullable: false),
                    DnsErrors = table.Column<long>(type: "bigint", nullable: false),
                    PermissionDenied = table.Column<long>(type: "bigint", nullable: false),
                    MonitorErrors = table.Column<long>(type: "bigint", nullable: false),
                    UnknownFailures = table.Column<long>(type: "bigint", nullable: false),
                    LatencySum = table.Column<double>(type: "float", nullable: false),
                    LatencyOverflow = table.Column<bool>(type: "bit", nullable: false),
                    LatencyMin = table.Column<double>(type: "float", nullable: true),
                    LatencyMax = table.Column<double>(type: "float", nullable: true),
                    FirstOccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingRollups", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            // Cross-module resource reference is owned by Network without a CLR module dependency.
            migrationBuilder.AddForeignKey(
                name: "FK_PingRollups_Devices_DeviceId",
                schema: "network",
                table: "PingRollups",
                column: "DeviceId",
                principalSchema: "resources",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.CreateIndex(
                name: "IX_PingSamples_Unaggregated",
                schema: "network",
                table: "PingSamples",
                column: "RowId",
                filter: "[Aggregated] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_PingRollups_DeviceId_WindowStart_Target_Location",
                schema: "network",
                table: "PingRollups",
                columns: new[] { "DeviceId", "WindowStart", "Target", "Location" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep the table locked through the migration transaction: a concurrent writer
            // must not insert a summary between the emptiness check and DROP TABLE.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [network].[PingRollups] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove nonempty Ping rollups; preserve historical data before rollback.', 1;");

            migrationBuilder.DropTable(
                name: "PingRollups",
                schema: "network");

            migrationBuilder.DropIndex(
                name: "IX_PingSamples_Unaggregated",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropColumn(
                name: "Aggregated",
                schema: "network",
                table: "PingSamples");
        }
    }
}
