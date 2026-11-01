using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PingExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Generation",
                schema: "network",
                table: "PingSamples",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                schema: "network",
                table: "PingSamples",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedAddress",
                schema: "network",
                table: "PingSamples",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Target",
                schema: "network",
                table: "PingSamples",
                type: "nvarchar(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PingSettings",
                schema: "network",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    FailureThreshold = table.Column<int>(type: "int", nullable: false),
                    SuccessThreshold = table.Column<int>(type: "int", nullable: false),
                    MaxConcurrency = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_PingSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "PingStateEvents",
                schema: "network",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Previous = table.Column<int>(type: "int", nullable: false),
                    Current = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingStateEvents", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "PingTargets",
                schema: "network",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    DeviceVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Successes = table.Column<int>(type: "int", nullable: false),
                    Failures = table.Column<int>(type: "int", nullable: false),
                    MeasuredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: true),
                    LatencyMilliseconds = table.Column<double>(type: "float", nullable: true),
                    ResolvedAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingTargets", x => new { x.DeviceId, x.Target })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PingSamples_DeviceId_Target_Generation",
                schema: "network",
                table: "PingSamples",
                columns: new[] { "DeviceId", "Target", "Generation" },
                unique: true,
                filter: "[Generation] IS NOT NULL AND [Target] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PingSettings_Name",
                schema: "network",
                table: "PingSettings",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PingSettings_RowId",
                schema: "network",
                table: "PingSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PingStateEvents_DeviceId_OccurredAt_RowId",
                schema: "network",
                table: "PingStateEvents",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_PingStateEvents_DeviceId_Target_Generation",
                schema: "network",
                table: "PingStateEvents",
                columns: new[] { "DeviceId", "Target", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PingTargets_Enabled_DueAt",
                schema: "network",
                table: "PingTargets",
                columns: new[] { "Enabled", "DueAt" });

            // Network owns these cross-module references; no Resources CLR entity is exposed.
            migrationBuilder.AddForeignKey("FK_PingTargets_Devices_DeviceId", "PingTargets", "DeviceId", "Devices",
                schema: "network", principalSchema: "resources", principalColumn: "Id", onDelete: ReferentialAction.NoAction);
            migrationBuilder.AddForeignKey("FK_PingStateEvents_Devices_DeviceId", "PingStateEvents", "DeviceId", "Devices",
                schema: "network", principalSchema: "resources", principalColumn: "Id", onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PingSettings",
                schema: "network");

            migrationBuilder.DropTable(
                name: "PingStateEvents",
                schema: "network");

            migrationBuilder.DropTable(
                name: "PingTargets",
                schema: "network");

            migrationBuilder.DropIndex(
                name: "IX_PingSamples_DeviceId_Target_Generation",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropColumn(
                name: "Generation",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropColumn(
                name: "ResolvedAddress",
                schema: "network",
                table: "PingSamples");

            migrationBuilder.DropColumn(
                name: "Target",
                schema: "network",
                table: "PingSamples");
        }
    }
}
