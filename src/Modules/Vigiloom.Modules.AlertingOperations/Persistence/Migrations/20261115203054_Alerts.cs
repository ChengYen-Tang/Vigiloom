using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Alerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Notifications",
                schema: "operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceOwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EvidenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RuleTestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "PingAlerts",
                schema: "operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Target = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingAlerts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "ServiceHealthAlerts",
                schema: "operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceHealthAlerts", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "PingAlertEvents",
                schema: "operations",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsRecovery = table.Column<bool>(type: "bit", nullable: false),
                    DeviceName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    GroupsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PingAlertEvents", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_PingAlertEvents_PingAlerts_AlertId",
                        column: x => x.AlertId,
                        principalSchema: "operations",
                        principalTable: "PingAlerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_MessageId",
                schema: "operations",
                table: "Notifications",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_OccurredAt_RowId",
                schema: "operations",
                table: "Notifications",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RowId",
                schema: "operations",
                table: "Notifications",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PingAlertEvents_AlertId_Generation",
                schema: "operations",
                table: "PingAlertEvents",
                columns: new[] { "AlertId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PingAlertEvents_MessageId",
                schema: "operations",
                table: "PingAlertEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PingAlertEvents_OccurredAt_RowId",
                schema: "operations",
                table: "PingAlertEvents",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_PingAlerts_DeviceId_OpenedAt",
                schema: "operations",
                table: "PingAlerts",
                columns: new[] { "DeviceId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PingAlerts_DeviceId_Target",
                schema: "operations",
                table: "PingAlerts",
                columns: new[] { "DeviceId", "Target" },
                unique: true,
                filter: "[RecoveredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PingAlerts_RowId",
                schema: "operations",
                table: "PingAlerts",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthAlerts_RowId",
                schema: "operations",
                table: "ServiceHealthAlerts",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthAlerts_ServiceId_OpenedAt",
                schema: "operations",
                table: "ServiceHealthAlerts",
                columns: new[] { "ServiceId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthAlerts_TargetId",
                schema: "operations",
                table: "ServiceHealthAlerts",
                column: "TargetId",
                unique: true,
                filter: "[RecoveredAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Notifications",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "PingAlertEvents",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "ServiceHealthAlerts",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "PingAlerts",
                schema: "operations");
        }
    }
}
