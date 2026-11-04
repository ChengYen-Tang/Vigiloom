using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServiceTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Services_SourceId_ServiceKey_Environment",
                schema: "service",
                table: "Services");

            migrationBuilder.DropIndex(
                name: "IX_Candidates_SourceId_ServiceKey_Environment",
                schema: "service",
                table: "Candidates");

            migrationBuilder.AddColumn<string>(
                name: "ServiceNamespace",
                schema: "service",
                table: "Services",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ServiceNamespace",
                schema: "service",
                table: "Candidates",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "ServiceNamespaceBytes",
                schema: "service",
                table: "Services",
                type: "varbinary(256)",
                nullable: false,
                computedColumnSql: "CONVERT(varbinary(256), [ServiceNamespace])",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ServiceNamespaceBytes",
                schema: "service",
                table: "Candidates",
                type: "varbinary(256)",
                nullable: false,
                computedColumnSql: "CONVERT(varbinary(256), [ServiceNamespace])",
                stored: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Instances_Id_ServiceId",
                schema: "service",
                table: "Instances",
                columns: new[] { "Id", "ServiceId" });

            migrationBuilder.CreateTable(
                name: "LogContexts",
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
                    table.PrimaryKey("PK_LogContexts", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_LogContexts_RowId_ServiceId", x => new { x.RowId, x.ServiceId });
                    table.CheckConstraint("CK_LogContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536");
                    table.ForeignKey(
                        name: "FK_LogContexts_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LogRecords",
                schema: "service",
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
                    TraceId = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    SpanId = table.Column<byte[]>(type: "binary(8)", nullable: true),
                    RecordPayload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogRecords", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_LogRecords_Payload", "DATALENGTH([RecordPayload]) <= 262144");
                    table.CheckConstraint("CK_LogRecords_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
                    table.ForeignKey(
                        name: "FK_LogRecords_Instances_InstanceId_ServiceId",
                        columns: x => new { x.InstanceId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "Instances",
                        principalColumns: new[] { "Id", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LogRecords_LogContexts_ContextId_ServiceId",
                        columns: x => new { x.ContextId, x.ServiceId },
                        principalSchema: "service",
                        principalTable: "LogContexts",
                        principalColumns: new[] { "RowId", "ServiceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LogRecords_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "service",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Services_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "service",
                table: "Services",
                columns: new[] { "SourceId", "ServiceNamespaceBytes", "ServiceKey", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "service",
                table: "Candidates",
                columns: new[] { "SourceId", "ServiceNamespaceBytes", "ServiceKey", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogContexts_ServiceId",
                schema: "service",
                table: "LogContexts",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ContextId_ServiceId",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "ContextId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_InstanceId_ServiceId",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "InstanceId", "ServiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_OccurredAt_RowId",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "ServiceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_LogRecords_ServiceId_TraceId_OccurredAt",
                schema: "service",
                table: "LogRecords",
                columns: new[] { "ServiceId", "TraceId", "OccurredAt" },
                filter: "[TraceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [service].[Candidates] WHERE DATALENGTH([ServiceNamespace]) > 0)
                   OR EXISTS (SELECT 1 FROM [service].[Services] WHERE DATALENGTH([ServiceNamespace]) > 0)
                    THROW 51000, 'Cannot remove service namespaces while namespaced identities exist.', 1;
                IF EXISTS (SELECT 1 FROM [service].[LogRecords]) OR EXISTS (SELECT 1 FROM [service].[LogContexts])
                    THROW 51001, 'Cannot remove telemetry tables while log data exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "LogRecords",
                schema: "service");

            migrationBuilder.DropTable(
                name: "LogContexts",
                schema: "service");

            migrationBuilder.DropIndex(
                name: "IX_Services_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "service",
                table: "Services");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Instances_Id_ServiceId",
                schema: "service",
                table: "Instances");

            migrationBuilder.DropIndex(
                name: "IX_Candidates_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "service",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "ServiceNamespaceBytes",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "ServiceNamespaceBytes",
                schema: "service",
                table: "Candidates");

            migrationBuilder.DropColumn(
                name: "ServiceNamespace",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "ServiceNamespace",
                schema: "service",
                table: "Candidates");

            migrationBuilder.CreateIndex(
                name: "IX_Services_SourceId_ServiceKey_Environment",
                schema: "service",
                table: "Services",
                columns: new[] { "SourceId", "ServiceKey", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Candidates_SourceId_ServiceKey_Environment",
                schema: "service",
                table: "Candidates",
                columns: new[] { "SourceId", "ServiceKey", "Environment" },
                unique: true);
        }
    }
}
