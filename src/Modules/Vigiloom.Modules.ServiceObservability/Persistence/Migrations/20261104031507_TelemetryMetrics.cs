using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MetricContexts",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourcePayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ScopePayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    MetricPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ResourceSchemaUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ScopeSchemaUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricContexts", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_MetricContexts_RowId_ServiceId", x => new { x.RowId, x.ServiceId });
                    table.CheckConstraint("CK_MetricContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536 AND DATALENGTH([MetricPayload]) <= 65536");
                    table.ForeignKey(
                        name: "FK_MetricContexts_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MetricPoints",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContextId = table.Column<long>(type: "bigint", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    PointPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricPoints", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_MetricPoints_Kind", "[Kind] BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_MetricPoints_Payload", "DATALENGTH([PointPayload]) <= 262144");
                    table.CheckConstraint("CK_MetricPoints_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
                    table.ForeignKey(
                        name: "FK_MetricPoints_Instances_InstanceId_ServiceId",
                        columns: x => new { x.InstanceId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "Instances",
                        principalColumns: new[] { "Id", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MetricPoints_MetricContexts_ContextId_ServiceId",
                        columns: x => new { x.ContextId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "MetricContexts",
                        principalColumns: new[] { "RowId", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MetricPoints_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MetricContexts_ServiceId",
                schema: "service",
                table: "MetricContexts",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ContextId_ServiceId",
                schema: "service",
                table: "MetricPoints",
                columns: new[] { "ContextId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_InstanceId_ServiceId",
                schema: "service",
                table: "MetricPoints",
                columns: new[] { "InstanceId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricPoints_ServiceId_OccurredAt_RowId",
                schema: "service",
                table: "MetricPoints",
                columns: new[] { "ServiceId", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [service].[MetricPoints]) OR EXISTS (SELECT 1 FROM [service].[MetricContexts])
                    THROW 51005, 'Cannot remove metric tables while metric data exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "MetricPoints",
                schema: "service");

            migrationBuilder.DropTable(
                name: "MetricContexts",
                schema: "service");
        }
    }
}
