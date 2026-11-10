using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostLogSourceAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_DeviceId_OccurredAt_RowId",
                schema: "agent",
                table: "HostLogSourceAudits",
                columns: new[] { "DeviceId", "OccurredAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_HostLogSourceAudits_SourceId",
                schema: "agent",
                table: "HostLogSourceAudits",
                column: "SourceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[HostLogSourceAudits] WITH (UPDLOCK, HOLDLOCK))
                    THROW 51000, 'Cannot remove host log source audit records during downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "HostLogSourceAudits",
                schema: "agent");
        }
    }
}
