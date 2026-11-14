using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DevicePublicationFence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Previous writers recorded time before commit. Treat existing periods as unconfirmed.
            migrationBuilder.Sql("UPDATE [resources].[Devices] WITH (TABLOCKX, HOLDLOCK) SET [PublicSince] = CONVERT(datetimeoffset, '9999-12-31T23:59:59.9999999+00:00') WHERE [IsPublic] = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [resources].[Devices] WITH (TABLOCKX, HOLDLOCK) WHERE [IsPublic] = 1) THROW 51007, 'Cannot downgrade active device publication periods.', 1;");
        }
    }
}
