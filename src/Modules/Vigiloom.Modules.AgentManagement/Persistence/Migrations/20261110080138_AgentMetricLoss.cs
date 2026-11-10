using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentMetricLoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MetricLossBatches",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MetricLossCompletenessUnknown",
                schema: "agent",
                table: "Agents",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MetricLossLedgerId",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetricLossRecords",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MetricLossReportEpoch",
                schema: "agent",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MetricLossReportSequence",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MetricLossReportedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MetricLossStartedAt",
                schema: "agent",
                table: "Agents",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [agent].[Agents] WITH (UPDLOCK, HOLDLOCK) WHERE [MetricLossLedgerId] IS NOT NULL)
                    THROW 51000, 'Cannot remove metric loss accounting while reports exist.', 1;
                """);
            migrationBuilder.DropColumn(
                name: "MetricLossBatches",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossCompletenessUnknown",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossLedgerId",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossRecords",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossReportEpoch",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossReportSequence",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossReportedAt",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "MetricLossStartedAt",
                schema: "agent",
                table: "Agents");
        }
    }
}
