using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AlertingOperations.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimelineLogReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LocalLogRowId",
                schema: "operations",
                table: "TimelineRecords",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalLogRowId",
                schema: "operations",
                table: "TimelineRecords");
        }
    }
}
