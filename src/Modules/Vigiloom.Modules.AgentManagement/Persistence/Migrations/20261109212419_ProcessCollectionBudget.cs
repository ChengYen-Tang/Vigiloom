using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProcessCollectionBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: false,
                defaultValue: 2000);

            migrationBuilder.AddColumn<int>(
                name: "ProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettings");

            migrationBuilder.DropColumn(
                name: "EffectiveProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettingAudits");

            migrationBuilder.DropColumn(
                name: "ProcessCollectionBudgetMs",
                schema: "agent",
                table: "MetricSettingAudits");
        }
    }
}
