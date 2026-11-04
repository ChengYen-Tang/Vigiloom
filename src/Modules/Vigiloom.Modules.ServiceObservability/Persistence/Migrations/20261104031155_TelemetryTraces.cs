using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryTraces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraceContexts",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourcePayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ScopePayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ResourceSchemaUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ScopeSchemaUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceContexts", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_TraceContexts_RowId_ServiceId", x => new { x.RowId, x.ServiceId });
                    table.CheckConstraint("CK_TraceContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536");
                    table.ForeignKey(
                        name: "FK_TraceContexts_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TraceSpans",
                schema: "service",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ContextId = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TraceId = table.Column<byte[]>(type: "binary(16)", nullable: false),
                    SpanId = table.Column<byte[]>(type: "binary(8)", nullable: false),
                    ParentSpanId = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    SpanPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceSpans", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_TraceSpans_Payload", "DATALENGTH([SpanPayload]) <= 262144");
                    table.CheckConstraint("CK_TraceSpans_Times", "[EndedAt] >= [StartedAt]");
                    table.CheckConstraint("CK_TraceSpans_Utc", "DATEPART(TZOFFSET, [StartedAt]) = 0 AND DATEPART(TZOFFSET, [EndedAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
                    table.ForeignKey(
                        name: "FK_TraceSpans_Instances_InstanceId_ServiceId",
                        columns: x => new { x.InstanceId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "Instances",
                        principalColumns: new[] { "Id", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceSpans_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TraceSpans_TraceContexts_ContextId_ServiceId",
                        columns: x => new { x.ContextId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "TraceContexts",
                        principalColumns: new[] { "RowId", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraceContexts_ServiceId",
                schema: "service",
                table: "TraceContexts",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_ContextId_ServiceId",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "ContextId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_InstanceId_ServiceId",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "InstanceId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_ServiceId_StartedAt_RowId",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "ServiceId", "StartedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_TraceSpans_ServiceId_TraceId_StartedAt_RowId",
                schema: "service",
                table: "TraceSpans",
                columns: new[] { "ServiceId", "TraceId", "StartedAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [service].[TraceSpans]) OR EXISTS (SELECT 1 FROM [service].[TraceContexts])
                    THROW 51003, 'Cannot remove trace tables while trace data exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "TraceSpans",
                schema: "service");

            migrationBuilder.DropTable(
                name: "TraceContexts",
                schema: "service");
        }
    }
}
