using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.ServiceObservability.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServicePublicationPeriod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicSince",
                schema: "service",
                table: "Services",
                type: "datetimeoffset",
                nullable: true);

            // Earlier versions did not record a public period. Do not expose older history.
            migrationBuilder.Sql("UPDATE [service].[Services] WITH (TABLOCKX, HOLDLOCK) SET [PublicSince] = CONVERT(datetimeoffset, '9999-12-31T23:59:59.9999999+00:00') WHERE [IsPublic] = 1;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Services_PublicPeriod",
                schema: "service",
                table: "Services",
                sql: "([IsPublic] = 0 AND [PublicSince] IS NULL) OR ([IsPublic] = 1 AND [PublicSince] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [service].[Services] WITH (TABLOCKX, HOLDLOCK) WHERE [PublicSince] IS NOT NULL) THROW 51006, 'Cannot discard service publication periods.', 1;");
            migrationBuilder.DropCheckConstraint(
                name: "CK_Services_PublicPeriod",
                schema: "service",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "PublicSince",
                schema: "service",
                table: "Services");
        }
    }
}
