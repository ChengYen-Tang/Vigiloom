using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServiceArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                schema: "service",
                table: "Services",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Services_Archived",
                schema: "service",
                table: "Services",
                sql: "[ArchivedAt] IS NULL OR ([IsPublic] = 0 AND [RevokedAt] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [service].[Services] WITH (TABLOCKX, HOLDLOCK) WHERE [ArchivedAt] IS NOT NULL) THROW 51000, 'Cannot roll back service archive while archived services exist.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Services_Archived",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                schema: "service",
                table: "Services");
        }
    }
}
