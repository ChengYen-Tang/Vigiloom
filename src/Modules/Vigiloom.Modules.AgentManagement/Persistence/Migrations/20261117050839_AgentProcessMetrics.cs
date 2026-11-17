using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentProcessMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "RuntimeCpuPercent",
                schema: "agent",
                table: "Agents",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RuntimeWorkingSetBytes",
                schema: "agent",
                table: "Agents",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeCpuPercent",
                schema: "agent",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "RuntimeWorkingSetBytes",
                schema: "agent",
                table: "Agents");
        }
    }
}
