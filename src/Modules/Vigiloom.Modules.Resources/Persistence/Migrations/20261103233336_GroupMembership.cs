using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GroupMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "resources",
                table: "Groups",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceGroups",
                schema: "resources",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceGroups", x => new { x.GroupId, x.ServiceId })
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_ServiceGroups_Groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "resources",
                        principalTable: "Groups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                schema: "resources",
                table: "Groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceGroups_ServiceId_GroupId",
                schema: "resources",
                table: "ServiceGroups",
                columns: new[] { "ServiceId", "GroupId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceGroups",
                schema: "resources");

            migrationBuilder.DropIndex(
                name: "IX_Groups_Name",
                schema: "resources",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "resources",
                table: "Groups");
        }
    }
}
