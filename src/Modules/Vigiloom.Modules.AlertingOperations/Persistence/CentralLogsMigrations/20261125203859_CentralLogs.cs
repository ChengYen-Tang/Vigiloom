using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.CentralLogsMigrations
{
    /// <inheritdoc />
    public partial class CentralLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "logs");

            migrationBuilder.CreateTable(
                name: "HostLogs",
                schema: "logs",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKind = table.Column<int>(type: "int", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ParserVersion = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EventId = table.Column<long>(type: "bigint", nullable: true),
                    Level = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    User = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Process = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    GapReason = table.Column<int>(type: "int", nullable: true),
                    GapAffectedCount = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostLogs", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_CentralHostLogs_Gap", "([GapReason] IS NULL AND [GapAffectedCount] IS NULL) OR\n([GapReason] IS NOT NULL AND [GapReason] BETWEEN 1 AND 5 AND\n ([GapAffectedCount] IS NULL OR [GapAffectedCount] > 0) AND [Content] = N'' AND\n [Provider] IS NULL AND [EventId] IS NULL AND [Level] IS NULL AND [User] IS NULL AND [Process] IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "LogContexts",
                schema: "logs",
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
                    table.PrimaryKey("PK_LogContexts", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_LogContexts_RowId_ServiceId", x => new { x.RowId, x.ServiceId });
                    table.CheckConstraint("CK_CentralLogContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536");
                });

            migrationBuilder.CreateTable(
                name: "LogRecords",
                schema: "logs",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContextId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedReceivedTime = table.Column<bool>(type: "bit", nullable: false),
                    UsedObservedTime = table.Column<bool>(type: "bit", nullable: false),
                    TraceId = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    SpanId = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    RecordPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogRecords", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_CentralLogRecords_Payload", "DATALENGTH([RecordPayload]) <= 262144");
                    table.CheckConstraint("CK_CentralLogRecords_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
                    table.ForeignKey(
                        name: "FK_LogRecords_LogContexts_ContextId_ServiceId",
                        columns: x => new { x.ContextId, x.ServiceId },
                        principalSchema: "logs",
                        principalTable: "LogContexts",
                        principalColumns: new[] { "RowId", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_MessageId",
                schema: "logs",
                table: "HostLogs",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_ReceivedAt_RowId",
                schema: "logs",
                table: "HostLogs",
                columns: new[] { "AgentId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_SourceId_OccurredAt_RowId",
                schema: "logs",
                table: "HostLogs",
                columns: new[] { "AgentId", "SourceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_SourceId_ReceivedAt_RowId",
                schema: "logs",
                table: "HostLogs",
                columns: new[] { "AgentId", "SourceId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_ReceivedAt_RowId",
                schema: "logs",
                table: "HostLogs",
                columns: new[] { "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ContextId_ServiceId",
                schema: "logs",
                table: "LogRecords",
                columns: new[] { "ContextId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_OccurredAt_RowId",
                schema: "logs",
                table: "LogRecords",
                columns: new[] { "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_OccurredAt_RowId",
                schema: "logs",
                table: "LogRecords",
                columns: new[] { "ServiceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_ReceivedAt",
                schema: "logs",
                table: "LogRecords",
                columns: new[] { "ServiceId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_TraceId_OccurredAt",
                schema: "logs",
                table: "LogRecords",
                columns: new[] { "ServiceId", "TraceId", "OccurredAt" },
                filter: "[TraceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostLogs",
                schema: "logs");

            migrationBuilder.DropTable(
                name: "LogRecords",
                schema: "logs");

            migrationBuilder.DropTable(
                name: "LogContexts",
                schema: "logs");
        }
    }
}
