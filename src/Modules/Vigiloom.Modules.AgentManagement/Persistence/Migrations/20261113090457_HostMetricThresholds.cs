using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostMetricThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                    table.ForeignKey(
                        name: "FK_HostMetricThresholds_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.AddForeignKey(
                name: "FK_HostMetricThresholds_Devices_DeviceId",
                schema: "agent", table: "HostMetricThresholds", column: "DeviceId",
                principalSchema: "resources", principalTable: "Devices", principalColumn: "Id");

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
                name: "IX_HostMetricThresholds_AgentId",
                schema: "agent",
                table: "HostMetricThresholds",
                column: "AgentId");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[HostMetricThresholds] WITH (TABLOCKX, HOLDLOCK)) OR EXISTS (SELECT 1 FROM [agent].[HostMetricThresholdAudits] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Threshold settings and audit records must be migrated before rollback.', 1;");
            migrationBuilder.DropTable(
                name: "HostMetricThresholdAudits",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "HostMetricThresholds",
                schema: "agent");
        }
    }
}
