using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentSamplingIoBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveIoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: false,
                defaultValue: 64);

            migrationBuilder.AddColumn<int>(
                name: "IoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // New audit revisions include the I/O field even when it has the default value.
            // Keep exclusive locks until the migration transaction ends so no policy or
            // audit can be written between the safety check and dropping the columns.
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [agent].[MetricSettings] WITH (TABLOCKX, HOLDLOCK) WHERE [IoLimitMiBPerMinute] IS NOT NULL) OR EXISTS (SELECT 1 FROM [agent].[MetricSettingAudits] WITH (TABLOCKX, HOLDLOCK)) THROW 51000, 'Cannot remove the I/O budget while settings or audit records exist.', 1;");
            migrationBuilder.DropColumn(
                name: "IoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettings");

            migrationBuilder.DropColumn(
                name: "EffectiveIoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");

            migrationBuilder.DropColumn(
                name: "IoLimitMiBPerMinute",
                schema: "agent",
                table: "MetricSettingAudits");
        }
    }
}
