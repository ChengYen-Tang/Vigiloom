using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplayClock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReplayClockFloor",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE a SET [ReplayClockFloor] = latest.[ReceivedAt]
                FROM [agent].[Agents] AS a
                CROSS APPLY (
                    SELECT MAX(b.[ReceivedAt]) AS [ReceivedAt]
                    FROM [agent].[ReceivedBatches] AS b WHERE b.[AgentId] = a.[Id]
                ) AS latest
                WHERE latest.[ReceivedAt] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayClockFloor",
                schema: "agent",
                table: "Agents");
        }
    }
}
