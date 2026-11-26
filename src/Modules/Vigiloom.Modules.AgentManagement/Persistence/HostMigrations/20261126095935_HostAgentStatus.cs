using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.HostMigrations
{
    /// <inheritdoc />
    public partial class HostAgentStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostAgentStatuses",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    HeartbeatSafetyIntervalSeconds = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    QueueReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QueueReportEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueueReportSequence = table.Column<long>(type: "bigint", nullable: true),
                    QueueTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    QueueTotalBatches = table.Column<long>(type: "bigint", nullable: true),
                    QueueQuarantinedBatches = table.Column<long>(type: "bigint", nullable: true),
                    QueueCapacityBytes = table.Column<long>(type: "bigint", nullable: true),
                    QueueCapacityBatches = table.Column<int>(type: "int", nullable: true),
                    QuarantineInvalidLocal = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineBadRequest = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineConflict = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineTooLarge = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineUnsupported = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineExpired = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineOther = table.Column<long>(type: "bigint", nullable: true),
                    QuarantineReportEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QuarantineReportSequence = table.Column<long>(type: "bigint", nullable: true),
                    QueueCapacityReportEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueueCapacityReportSequence = table.Column<long>(type: "bigint", nullable: true),
                    QueueCapacityReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QueueCapacityAppliedRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    QueueCapacityAppliedBytes = table.Column<long>(type: "bigint", nullable: true),
                    QueueCapacityAppliedBatches = table.Column<int>(type: "int", nullable: true),
                    QueueCapacityAppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    QueueCapacityFailure = table.Column<int>(type: "int", nullable: true),
                    ConfigurationReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AppliedConfigurationRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AppliedHeartbeatSeconds = table.Column<int>(type: "int", nullable: true),
                    AppliedSettingsPollSeconds = table.Column<int>(type: "int", nullable: true),
                    AppliedBatchIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ConfigurationAppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfigurationFailure = table.Column<int>(type: "int", nullable: true),
                    MetricLossReportEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MetricLossReportSequence = table.Column<long>(type: "bigint", nullable: true),
                    MetricLossReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MetricLossLedgerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MetricLossStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MetricLossBatches = table.Column<long>(type: "bigint", nullable: true),
                    MetricLossRecords = table.Column<long>(type: "bigint", nullable: true),
                    MetricLossCompletenessUnknown = table.Column<bool>(type: "bit", nullable: true),
                    LogStatusReportEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LogStatusReportSequence = table.Column<long>(type: "bigint", nullable: true),
                    LogStatusReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LogStatusJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: true),
                    InventoryConfigurationEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InventoryConfigurationSequence = table.Column<long>(type: "bigint", nullable: true),
                    InventoryConfigurationReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    InventoryConfigurationJson = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    IntegrityConfigurationEpoch = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IntegrityConfigurationSequence = table.Column<long>(type: "bigint", nullable: true),
                    IntegrityConfigurationReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IntegrityConfigurationJson = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    BaselineConfigurationReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BaselineConfigurationJson = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RuntimeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RuntimeIdentifier = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                    RuntimeReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RuntimeCpuPercent = table.Column<double>(type: "float", nullable: true),
                    RuntimeWorkingSetBytes = table.Column<long>(type: "bigint", nullable: true),
                    RuntimeModuleFailuresJson = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostAgentStatuses", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostAgentStatuses_DeviceId",
                schema: "agent",
                table: "HostAgentStatuses",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_HostAgentStatuses_RowId",
                schema: "agent",
                table: "HostAgentStatuses",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostAgentStatuses",
                schema: "agent");
        }
    }
}
