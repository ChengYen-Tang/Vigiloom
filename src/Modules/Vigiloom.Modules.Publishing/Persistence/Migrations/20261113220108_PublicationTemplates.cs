using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Publishing.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "publishing");

            migrationBuilder.CreateTable(
                name: "Templates",
                schema: "publishing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_CI_AS_SC"),
                    ShowLatency = table.Column<bool>(type: "bit", nullable: false),
                    ShowObservedAt = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Templates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Templates_DefaultActive", "[IsDefault] = 0 OR [IsRetired] = 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Templates_IsDefault",
                schema: "publishing",
                table: "Templates",
                column: "IsDefault",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_Name",
                schema: "publishing",
                table: "Templates",
                column: "Name",
                unique: true,
                filter: "[IsRetired] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Templates_RowId",
                schema: "publishing",
                table: "Templates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM [publishing].[Templates] WITH (TABLOCKX, HOLDLOCK)) THROW 51004, 'Cannot remove saved publication templates.', 1;");
            migrationBuilder.DropTable(
                name: "Templates",
                schema: "publishing");
        }
    }
}
