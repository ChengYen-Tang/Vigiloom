using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.NetworkMonitoring.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PingTransitionDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastTransitionGeneration",
                schema: "network",
                table: "PingTargets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
            migrationBuilder.Sql("UPDATE [network].[PingTargets] SET [LastTransitionGeneration] = [Generation]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTransitionGeneration",
                schema: "network",
                table: "PingTargets");
        }
    }
}
