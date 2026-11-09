using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentReplayGenerations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReceiptRetentionDays",
                schema: "agent",
                table: "ReplayPolicies",
                type: "int",
                nullable: false,
                defaultValue: 7);

            migrationBuilder.AddColumn<int>(
                name: "ReplayWindowSeconds",
                schema: "agent",
                table: "ReplayPolicies",
                type: "int",
                nullable: false,
                defaultValue: 86400);

            migrationBuilder.CreateTable(
                name: "ReplayGenerations",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WindowSeconds = table.Column<int>(type: "int", nullable: false),
                    ValidThrough = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayGenerations", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_ReplayGenerations_WindowSeconds", "[WindowSeconds] BETWEEN 1 AND 2592000");
                    table.ForeignKey(
                        name: "FK_ReplayGenerations_ReplayPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalSchema: "agent",
                        principalTable: "ReplayPolicies",
                        principalColumn: "Id");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_ReceiptRetentionDays",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "[ReceiptRetentionDays] BETWEEN 1 AND 3650");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReplayPolicies_ReplayWindowSeconds",
                schema: "agent",
                table: "ReplayPolicies",
                sql: "[ReplayWindowSeconds] BETWEEN 1 AND 2592000");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayGenerations_PolicyId_ValidThrough",
                schema: "agent",
                table: "ReplayGenerations",
                columns: new[] { "PolicyId", "ValidThrough" })
                .Annotation("SqlServer:Include", new[] { "WindowSeconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older receivers cannot honor changed windows, previous obligations or cleanup fences.
            // As with the rest of the migration workflow, receiver processes must be stopped and drained.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[ReplayPolicies] WITH (UPDLOCK, HOLDLOCK)
                    WHERE [ReplayWindowSeconds] <> 86400 OR [ReceiptRetentionDays] <> 7 OR [CleanupCutoverAt] IS NOT NULL)
                   OR EXISTS (SELECT 1 FROM [agent].[ReplayGenerations] WITH (UPDLOCK, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [agent].[Agents] WITH (UPDLOCK, HOLDLOCK) WHERE [ReplayCleanedBefore] IS NOT NULL)
                    THROW 51000, 'An active inbox replay configuration cannot be downgraded.', 1;
                """);
            migrationBuilder.DropTable(
                name: "ReplayGenerations",
                schema: "agent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_ReceiptRetentionDays",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReplayPolicies_ReplayWindowSeconds",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropColumn(
                name: "ReceiptRetentionDays",
                schema: "agent",
                table: "ReplayPolicies");

            migrationBuilder.DropColumn(
                name: "ReplayWindowSeconds",
                schema: "agent",
                table: "ReplayPolicies");
        }
    }
}
