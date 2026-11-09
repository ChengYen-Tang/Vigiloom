using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentUploadBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveUploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: false,
                defaultValue: 16);

            migrationBuilder.AddColumn<int>(
                name: "UploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettings");

            migrationBuilder.DropColumn(
                name: "EffectiveUploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");

            migrationBuilder.DropColumn(
                name: "UploadLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");
        }
    }
}
