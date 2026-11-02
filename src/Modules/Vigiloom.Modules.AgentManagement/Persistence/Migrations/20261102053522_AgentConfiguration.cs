using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppliedBatchIntervalSeconds",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppliedConfigurationRevision",
                schema: "agent",
                table: "Agents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppliedHeartbeatSeconds",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AppliedSettingsPollSeconds",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfigurationAppliedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConfigurationFailure",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfigurationReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HeartbeatSafetyIntervalSeconds",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "Settings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HeartbeatSeconds = table.Column<int>(type: "int", nullable: true),
                    SettingsPollSeconds = table.Column<int>(type: "int", nullable: true),
                    BatchIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_AgentSettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "SettingAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HeartbeatSeconds = table.Column<int>(type: "int", nullable: true),
                    SettingsPollSeconds = table.Column<int>(type: "int", nullable: true),
                    BatchIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    EffectiveHeartbeatSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveSettingsPollSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveBatchIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_SettingAudits_Settings_SettingId",
                        column: x => x.SettingId,
                        principalSchema: "agent",
                        principalTable: "Settings",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Agents_HeartbeatSafety",
                schema: "agent",
                table: "Agents",
                sql: "[HeartbeatSafetyIntervalSeconds] BETWEEN 10 AND 300");

            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "SettingAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_SettingId",
                schema: "agent",
                table: "SettingAudits",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_DeviceId",
                schema: "agent",
                table: "Settings",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settings_RowId",
                schema: "agent",
                table: "Settings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            // Cross-module references remain in migrations, without an EF dependency on Resources.
            migrationBuilder.AddForeignKey(
                name: "FK_Settings_Devices_DeviceId", schema: "agent", table: "Settings", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
            migrationBuilder.AddForeignKey(
                name: "FK_SettingAudits_Devices_DeviceId", schema: "agent", table: "SettingAudits", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_SettingAudits_Devices_DeviceId", schema: "agent", table: "SettingAudits");
            migrationBuilder.DropForeignKey(name: "FK_Settings_Devices_DeviceId", schema: "agent", table: "Settings");
            migrationBuilder.DropTable(
                name: "SettingAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "agent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Agents_HeartbeatSafety",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "AppliedBatchIntervalSeconds",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "AppliedConfigurationRevision",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "AppliedHeartbeatSeconds",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "AppliedSettingsPollSeconds",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ConfigurationAppliedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ConfigurationFailure",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "ConfigurationReportedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "HeartbeatSafetyIntervalSeconds",
                schema: "agent",
                table: "Agents");
        }
    }
}
