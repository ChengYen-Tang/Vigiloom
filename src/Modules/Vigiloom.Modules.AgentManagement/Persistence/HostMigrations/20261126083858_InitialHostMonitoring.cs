using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.HostMigrations
{
    /// <inheritdoc />
    public partial class InitialHostMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "agent");

            migrationBuilder.CreateTable(
                name: "HostMetricRetentionSettings",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Slot = table.Column<int>(type: "int", nullable: false),
                    RawDays = table.Column<int>(type: "int", nullable: false),
                    RollupDays = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetricRetentionSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Days", "[RawDays] BETWEEN 1 AND 3650 AND [RollupDays] BETWEEN [RawDays] AND 3650");
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_HostMetricRetentionSettings_Slot", "[Slot] = 1");
                });

            migrationBuilder.CreateTable(
                name: "HostMetricRollups",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SourceBytes = table.Column<byte[]>(type: "varbinary(256)", nullable: false, computedColumnSql: "CONVERT(varbinary(256), [Source])", stored: true),
                    DimensionBytes = table.Column<byte[]>(type: "varbinary(512)", nullable: true, computedColumnSql: "CONVERT(varbinary(512), [Dimension])", stored: true),
                    WindowStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Minimum = table.Column<double>(type: "float", nullable: false),
                    Maximum = table.Column<double>(type: "float", nullable: false),
                    Sum = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetricRollups", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "HostMetrics",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Dimension = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Value = table.Column<double>(type: "float", nullable: false),
                    Aggregated = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostMetrics", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "ReceivedBatches",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TrustedReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ItemCount = table.Column<int>(type: "int", nullable: false),
                    ByteCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedBatches", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "RuntimeObservations",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Architecture = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RuntimeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeObservations", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRetentionSettings_RowId",
                schema: "agent",
                table: "HostMetricRetentionSettings",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRetentionSettings_Slot",
                schema: "agent",
                table: "HostMetricRetentionSettings",
                column: "Slot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRollups_AgentId_Kind_WindowStart_SourceBytes_DimensionBytes",
                schema: "agent",
                table: "HostMetricRollups",
                columns: new[] { "AgentId", "Kind", "WindowStart", "SourceBytes", "DimensionBytes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRollups_Retention",
                schema: "agent",
                table: "HostMetricRollups",
                columns: new[] { "WindowStart", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_AgentId_Kind_OccurredAt_RowId",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "AgentId", "Kind", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_AgentId_MessageId",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_AgentId_OccurredAt_RowId",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "AgentId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_Retention",
                schema: "agent",
                table: "HostMetrics",
                columns: new[] { "OccurredAt", "RowId" },
                filter: "[Aggregated] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_Unaggregated",
                schema: "agent",
                table: "HostMetrics",
                column: "RowId",
                filter: "[Aggregated] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedBatches_AgentId_BatchId",
                schema: "agent",
                table: "ReceivedBatches",
                columns: new[] { "AgentId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedBatches_AgentId_ReceivedAt_RowId",
                schema: "agent",
                table: "ReceivedBatches",
                columns: new[] { "AgentId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeObservations_AgentId_MessageId",
                schema: "agent",
                table: "RuntimeObservations",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeObservations_AgentId_OccurredAt_RowId",
                schema: "agent",
                table: "RuntimeObservations",
                columns: new[] { "AgentId", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostMetricRetentionSettings",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostMetricRollups",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostMetrics",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "ReceivedBatches",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "RuntimeObservations",
                schema: "agent");
        }
    }
}
