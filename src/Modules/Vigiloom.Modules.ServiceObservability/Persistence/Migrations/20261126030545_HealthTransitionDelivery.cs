using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HealthTransitionDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastTransitionGeneration",
                schema: "service",
                table: "HealthSchedules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
            migrationBuilder.Sql("UPDATE [service].[HealthSchedules] SET [LastTransitionGeneration] = [Generation]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTransitionGeneration",
                schema: "service",
                table: "HealthSchedules");
        }
    }
}
