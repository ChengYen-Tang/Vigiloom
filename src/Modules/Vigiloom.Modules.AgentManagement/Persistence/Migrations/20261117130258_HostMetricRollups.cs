using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HostMetricRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Aggregated",
                schema: "agent",
                table: "HostMetrics",
                type: "bit",
                nullable: false,
                defaultValue: false);

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
                    table.ForeignKey(
                        name: "FK_HostMetricRollups_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostMetrics_Unaggregated",
                schema: "agent",
                table: "HostMetrics",
                column: "RowId",
                filter: "[Aggregated] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_HostMetricRollups_AgentId_Kind_WindowStart_SourceBytes_DimensionBytes",
                schema: "agent",
                table: "HostMetricRollups",
                columns: new[] { "AgentId", "Kind", "WindowStart", "SourceBytes", "DimensionBytes" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostMetricRollups",
                schema: "agent");

            migrationBuilder.DropIndex(
                name: "IX_HostMetrics_Unaggregated",
                schema: "agent",
                table: "HostMetrics");

            migrationBuilder.DropColumn(
                name: "Aggregated",
                schema: "agent",
                table: "HostMetrics");
        }
    }
}
