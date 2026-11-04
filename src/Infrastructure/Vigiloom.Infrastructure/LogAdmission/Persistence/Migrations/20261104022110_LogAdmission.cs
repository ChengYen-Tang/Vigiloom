using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Infrastructure.LogAdmission.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LogAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "logquota");

            migrationBuilder.CreateTable(
                name: "Buckets",
                schema: "logquota",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Microtokens = table.Column<long>(type: "bigint", nullable: false),
                    LastRefill = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PolicyVersion = table.Column<byte[]>(type: "varbinary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buckets", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_LogQuotaBucket_Kind", "[Kind] IN (0, 1, 2)");
                    table.CheckConstraint("CK_LogQuotaBucket_Tokens", "[Microtokens] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                schema: "logquota",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceRate = table.Column<int>(type: "int", nullable: false),
                    SourceBurst = table.Column<int>(type: "int", nullable: false),
                    GlobalRate = table.Column<int>(type: "int", nullable: false),
                    GlobalBurst = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_LogQuotaPolicy_Limits", "[SourceRate] > 0 AND [SourceBurst] > 0 AND [GlobalRate] > 0 AND [GlobalBurst] > 0");
                    table.CheckConstraint("CK_LogQuotaPolicy_Name", "[Name] = 'central'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Buckets_Kind_SourceId",
                schema: "logquota",
                table: "Buckets",
                columns: new[] { "Kind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_Name",
                schema: "logquota",
                table: "Policies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_RowId",
                schema: "logquota",
                table: "Policies",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Buckets",
                schema: "logquota");

            migrationBuilder.DropTable(
                name: "Policies",
                schema: "logquota");
        }
    }
}
