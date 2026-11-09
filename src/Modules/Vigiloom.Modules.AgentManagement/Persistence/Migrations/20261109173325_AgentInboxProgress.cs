using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInboxProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InboxDeletedTotal",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InboxLastDeletedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InboxLastDeletedCount",
                schema: "agent",
                table: "Agents",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[Agents] WHERE [InboxDeletedTotal] <> 0 OR [ReplayCleanedBefore] IS NOT NULL) THROW 51000, 'Inbox cleanup progress cannot be downgraded after cleanup.', 1;");
            migrationBuilder.DropColumn(
                name: "InboxDeletedTotal",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InboxLastDeletedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "InboxLastDeletedCount",
                schema: "agent",
                table: "Agents");
        }
    }
}
