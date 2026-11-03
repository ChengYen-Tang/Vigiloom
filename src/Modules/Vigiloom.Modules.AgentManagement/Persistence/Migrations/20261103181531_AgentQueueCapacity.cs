using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentQueueCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueCapacityAppliedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueueCapacityAppliedBatches",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueCapacityAppliedBytes",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueueCapacityAppliedRevision",
                schema: "agent",
                table: "Agents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueueCapacityFailure",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QueueCapacityReportEpoch",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueCapacityReportSequence",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueueCapacityReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QueueCapacitySettings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayloadBytes = table.Column<long>(type: "bigint", nullable: true),
                    BatchCount = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueCapacitySettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_QueueCapacitySettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

            migrationBuilder.CreateTable(
                name: "QueueCapacityAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PayloadBytes = table.Column<long>(type: "bigint", nullable: true),
                    BatchCount = table.Column<int>(type: "int", nullable: true),
                    EffectivePayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    EffectiveBatchCount = table.Column<int>(type: "int", nullable: false),
                    EffectiveRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueCapacityAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_QueueCapacityAudits_QueueCapacitySettings_SettingId",
                        column: x => x.SettingId,
                        principalSchema: "agent",
                        principalTable: "QueueCapacitySettings",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueueCapacityAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "QueueCapacityAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueCapacityAudits_OccurredAt_RowId",
                schema: "agent",
                table: "QueueCapacityAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueCapacityAudits_SettingId",
                schema: "agent",
                table: "QueueCapacityAudits",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_QueueCapacitySettings_DeviceId",
                schema: "agent",
                table: "QueueCapacitySettings",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueCapacitySettings_RowId",
                schema: "agent",
                table: "QueueCapacitySettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.AddForeignKey(
                name: "FK_QueueCapacitySettings_Devices_DeviceId", schema: "agent", table: "QueueCapacitySettings", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
            migrationBuilder.AddForeignKey(
                name: "FK_QueueCapacityAudits_Devices_DeviceId", schema: "agent", table: "QueueCapacityAudits", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_QueueCapacityAudits_Devices_DeviceId", schema: "agent", table: "QueueCapacityAudits");
            migrationBuilder.DropForeignKey(name: "FK_QueueCapacitySettings_Devices_DeviceId", schema: "agent", table: "QueueCapacitySettings");
            migrationBuilder.DropTable(
                name: "QueueCapacityAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "QueueCapacitySettings",
                schema: "agent");

            migrationBuilder.DropColumn(
                name: "QueueCapacityAppliedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityAppliedBatches",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityAppliedBytes",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityAppliedRevision",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityFailure",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityReportEpoch",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityReportSequence",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "QueueCapacityReportedAt",
                schema: "agent",
                table: "Agents");
        }
    }
}
