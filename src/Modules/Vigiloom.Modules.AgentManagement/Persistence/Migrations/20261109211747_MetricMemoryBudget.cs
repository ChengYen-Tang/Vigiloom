using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricMemoryBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveWorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: false,
                defaultValue: 512);

            migrationBuilder.AddColumn<int>(
                name: "WorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettings");

            migrationBuilder.DropColumn(
                name: "EffectiveWorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettingAudits");

            migrationBuilder.DropColumn(
                name: "WorkingSetLimitMiB",
                schema: "agent",
                table: "MetricSettingAudits");
        }
    }
}
