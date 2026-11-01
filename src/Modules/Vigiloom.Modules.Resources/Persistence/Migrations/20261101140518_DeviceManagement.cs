using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                schema: "resources",
                table: "Devices",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "DeviceTargets",
                schema: "resources",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTargets", x => new { x.DeviceId, x.Address })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_DeviceTargets_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "resources",
                        principalTable: "Devices",
                        principalColumn: "Id");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceTargets",
                schema: "resources");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                schema: "resources",
                table: "Devices");
        }
    }
}
