using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.Resources.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationCandidates",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ServiceNamespace = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, defaultValue: "", collation: "Latin1_General_100_BIN2"),
                    ServiceNamespaceBytes = table.Column<byte[]>(type: "varbinary(256)", nullable: false, computedColumnSql: "CONVERT(varbinary(256), [ServiceNamespace])", stored: true),
                    Environment = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FirstSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationCandidates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationServices",
                schema: "resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ServiceKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ServiceNamespace = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, defaultValue: "", collation: "Latin1_General_100_BIN2"),
                    ServiceNamespaceBytes = table.Column<byte[]>(type: "varbinary(256)", nullable: false, computedColumnSql: "CONVERT(varbinary(256), [ServiceNamespace])", stored: true),
                    Environment = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    PublicSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PublicDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PublicPolicyVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationServices", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_ApplicationServices_Archived", "[ArchivedAt] IS NULL OR ([IsPublic] = 0 AND [RevokedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_ApplicationServices_PublicPeriod", "([IsPublic] = 0 AND [PublicSince] IS NULL) OR ([IsPublic] = 1 AND [PublicSince] IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationCandidates_LastSeen",
                schema: "resources",
                table: "ApplicationCandidates",
                column: "LastSeen");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationCandidates_RowId",
                schema: "resources",
                table: "ApplicationCandidates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationCandidates_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "resources",
                table: "ApplicationCandidates",
                columns: new[] { "SourceId", "ServiceNamespaceBytes", "ServiceKey", "Environment" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationServices_CandidateId",
                schema: "resources",
                table: "ApplicationServices",
                column: "CandidateId",
                unique: true,
                filter: "[CandidateId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationServices_IsPublic_PublicDisplayName_RowId",
                schema: "resources",
                table: "ApplicationServices",
                columns: new[] { "IsPublic", "PublicDisplayName", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationServices_RowId",
                schema: "resources",
                table: "ApplicationServices",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationServices_SourceId_ServiceNamespaceBytes_ServiceKey_Environment",
                schema: "resources",
                table: "ApplicationServices",
                columns: new[] { "SourceId", "ServiceNamespaceBytes", "ServiceKey", "Environment" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationCandidates",
                schema: "resources");

            migrationBuilder.DropTable(
                name: "ApplicationServices",
                schema: "resources");
        }
    }
}
