using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SimplifyAgentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackageSources",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "UpdateRoots",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "UpdateTrustStates",
                schema: "agent");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_Size",
                schema: "agent",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "Listed",
                schema: "agent",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "Revoked",
                schema: "agent",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "VerifiedUntil",
                schema: "agent",
                table: "Packages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_Size",
                schema: "agent",
                table: "Packages",
                sql: "[Size] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Packages_Size",
                schema: "agent",
                table: "Packages");

            migrationBuilder.AddColumn<bool>(
                name: "Listed",
                schema: "agent",
                table: "Packages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Revoked",
                schema: "agent",
                table: "Packages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VerifiedUntil",
                schema: "agent",
                table: "Packages",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "PackageSources",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseUri = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", maxLength: 8192, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackageSources", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_PackageSources_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_PackageSources_Scope", "[Scope] = 1");
                });

            migrationBuilder.CreateTable(
                name: "UpdateRoots",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    Metadata = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateRoots", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_UpdateRoots_MetadataSize", "DATALENGTH([Metadata]) BETWEEN 1 AND 1048576");
                    table.CheckConstraint("CK_UpdateRoots_Version", "[Version] > 0");
                });

            migrationBuilder.CreateTable(
                name: "UpdateTrustStates",
                schema: "agent",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    Snapshot = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateTrustStates", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_UpdateTrustStates_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_UpdateTrustStates_Scope", "[Scope] = 1");
                    table.CheckConstraint("CK_UpdateTrustStates_SnapshotSize", "DATALENGTH([Snapshot]) BETWEEN 1 AND 6291456");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Packages_Size",
                schema: "agent",
                table: "Packages",
                sql: "[Size] BETWEEN 1 AND 2147483648");

            migrationBuilder.CreateIndex(
                name: "IX_PackageSources_RowId",
                schema: "agent",
                table: "PackageSources",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PackageSources_Scope",
                schema: "agent",
                table: "PackageSources",
                column: "Scope",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateRoots_Version",
                schema: "agent",
                table: "UpdateRoots",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTrustStates_RowId",
                schema: "agent",
                table: "UpdateTrustStates",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_UpdateTrustStates_Scope",
                schema: "agent",
                table: "UpdateTrustStates",
                column: "Scope",
                unique: true);
        }
    }
}
