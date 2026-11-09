using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentLastBatchProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBatchEnqueuedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastBatchReceivedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE a SET [LastBatchEnqueuedAt] = b.[EnqueuedAt], [LastBatchReceivedAt] = b.[ReceivedAt]
                FROM [agent].[Agents] AS a
                CROSS APPLY (SELECT TOP (1) [EnqueuedAt], [ReceivedAt] FROM [agent].[ReceivedBatches]
                    WHERE [AgentId] = a.[Id] ORDER BY [ReceivedAt] DESC, [RowId] DESC) AS b;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[Agents] WHERE [ReplayCleanedBefore] IS NOT NULL) THROW 51000, 'Inbox diagnostics cannot be downgraded after cleanup.', 1;");
            migrationBuilder.DropColumn(
                name: "LastBatchEnqueuedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "LastBatchReceivedAt",
                schema: "agent",
                table: "Agents");
        }
    }
}
