using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricCpuBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveCpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: false,
                defaultValue: 3000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettings");

            migrationBuilder.DropColumn(
                name: "CpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");

            migrationBuilder.DropColumn(
                name: "EffectiveCpuMillisecondsPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");
        }
    }
}
