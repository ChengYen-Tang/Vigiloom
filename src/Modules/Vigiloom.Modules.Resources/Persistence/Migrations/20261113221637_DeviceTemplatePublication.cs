using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceTemplatePublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicSince",
                schema: "resources",
                table: "Devices",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PublicTemplateId",
                schema: "resources",
                table: "Devices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_PublicTemplateId",
                schema: "resources",
                table: "Devices",
                column: "PublicTemplateId",
                filter: "[PublicTemplateId] IS NOT NULL");

            // Existing publications have no trustworthy earlier public period.
            migrationBuilder.Sql("UPDATE [resources].[Devices] SET [PublicSince] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [IsPublic] = 1;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Devices_PublicPeriod",
                schema: "resources",
                table: "Devices",
                sql: "([IsPublic] = 0 AND [PublicSince] IS NULL) OR ([IsPublic] = 1 AND [PublicSince] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [resources].[Devices] WITH (TABLOCKX, HOLDLOCK) WHERE [PublicTemplateId] IS NOT NULL OR [PublicSince] IS NOT NULL) THROW 51005, 'Cannot discard device publication periods or template assignments.', 1;");
            migrationBuilder.DropIndex(
                name: "IX_Devices_PublicTemplateId",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Devices_PublicPeriod",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PublicSince",
                schema: "resources",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "PublicTemplateId",
                schema: "resources",
                table: "Devices");
        }
    }
}
