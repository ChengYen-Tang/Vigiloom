using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.HostMigrations
{
    /// <inheritdoc />
    public partial class HostSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostLogSources",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Target = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    TextMode = table.Column<int>(type: "int", nullable: true),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostLogSources", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "HostMetricThresholds",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SeriesHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Comparison = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<double>(type: "float", nullable: false),
                    FreshnessSeconds = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetricThresholds", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HostMetricThresholds_Comparison", "[Comparison] IN (1, 2)");
                    table.CheckConstraint("CK_HostMetricThresholds_Freshness", "[FreshnessSeconds] BETWEEN 30 AND 86400");
                    table.CheckConstraint("CK_HostMetricThresholds_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_HostMetricThresholds_Value", "[Value] >= 0 AND [Value] <= 1e30");
                });

            migrationBuilder.CreateTable(
                name: "MetricSettings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    HostIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ProcessIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    CpuMillisecondsPerMinute = table.Column<int>(type: "int", nullable: true),
                    WorkingSetLimitMiB = table.Column<int>(type: "int", nullable: true),
                    ProcessCollectionBudgetMs = table.Column<int>(type: "int", nullable: true),
                    IoLimitMiBPerMinute = table.Column<int>(type: "int", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_MetricSettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                });

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
                name: "HostLogSourceAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostLogSourceAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_HostLogSourceAudits_HostLogSources_SourceId",
                        column: x => x.SourceId,
                        principalSchema: "agent",
                        principalTable: "HostLogSources",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "HostMetricThresholdAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ThresholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    Comparison = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<double>(type: "float", nullable: false),
                    FreshnessSeconds = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetricThresholdAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_HostMetricThresholdAudits_HostMetricThresholds_ThresholdId",
                        column: x => x.ThresholdId,
                        principalSchema: "agent",
                        principalTable: "HostMetricThresholds",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MetricSettingAudits",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HostIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    ProcessIntervalSeconds = table.Column<int>(type: "int", nullable: true),
                    CpuMillisecondsPerMinute = table.Column<int>(type: "int", nullable: true),
                    WorkingSetLimitMiB = table.Column<int>(type: "int", nullable: true),
                    ProcessCollectionBudgetMs = table.Column<int>(type: "int", nullable: true),
                    IoLimitMiBPerMinute = table.Column<int>(type: "int", nullable: true),
                    EffectiveHostIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveProcessIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    EffectiveCpuMillisecondsPerMinute = table.Column<int>(type: "int", nullable: false, defaultValue: 3000),
                    EffectiveWorkingSetLimitMiB = table.Column<int>(type: "int", nullable: false, defaultValue: 512),
                    EffectiveProcessCollectionBudgetMs = table.Column<int>(type: "int", nullable: false, defaultValue: 2000),
                    EffectiveIoLimitMiBPerMinute = table.Column<int>(type: "int", nullable: false, defaultValue: 64),
                    EffectiveRevision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ChangeVersion = table.Column<byte[]>(type: "binary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSettingAudits", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_MetricSettingAudits_MetricSettings_SettingId",
                        column: x => x.SettingId,
                        principalSchema: "agent",
                        principalTable: "MetricSettings",
                        principalColumn: "Id");
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

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogSourceAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogSourceAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_SourceId",
                schema: "agent",
                table: "HostLogSourceAudits",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSources_DeviceId_RetiredAt",
                schema: "agent",
                table: "HostLogSources",
                columns: new[] { "DeviceId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSources_RowId",
                schema: "agent",
                table: "HostLogSources",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricThresholdAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "HostMetricThresholdAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricThresholdAudits_ThresholdId",
                schema: "agent",
                table: "HostMetricThresholdAudits",
                column: "ThresholdId");

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricThresholds_DeviceId_SeriesHash",
                schema: "agent",
                table: "HostMetricThresholds",
                columns: new[] { "DeviceId", "SeriesHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricThresholds_RowId",
                schema: "agent",
                table: "HostMetricThresholds",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "MetricSettingAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_OccurredAt_RowId",
                schema: "agent",
                table: "MetricSettingAudits",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettingAudits_SettingId",
                schema: "agent",
                table: "MetricSettingAudits",
                column: "SettingId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettings_DeviceId",
                schema: "agent",
                table: "MetricSettings",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetricSettings_RowId",
                schema: "agent",
                table: "MetricSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

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

            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "SettingAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_SettingAudits_OccurredAt_RowId",
                schema: "agent",
                table: "SettingAudits",
                columns: new[] { "OccurredAt", "RowId" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostLogSourceAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostMetricThresholdAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "MetricSettingAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "QueueCapacityAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "SettingAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostLogSources",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostMetricThresholds",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "MetricSettings",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "QueueCapacitySettings",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "Settings",
                schema: "agent");
        }
    }
}
