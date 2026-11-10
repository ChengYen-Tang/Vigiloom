using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostLogs",
                schema: "agent",
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
                    Process = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostLogs", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_HostLogs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_MessageId",
                schema: "agent",
                table: "HostLogs",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_ReceivedAt_RowId",
                schema: "agent",
                table: "HostLogs",
                columns: new[] { "AgentId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogs_AgentId_SourceId_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogs",
                columns: new[] { "AgentId", "SourceId", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[HostLogs] WITH (UPDLOCK, HOLDLOCK))
                    THROW 51000, 'Cannot remove host logs while records exist.', 1;
                """);
            migrationBuilder.DropTable(
                name: "HostLogs",
                schema: "agent");
        }
    }
}
