using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Infrastructure.LogAdmission.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TraceAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TraceBuckets",
                schema: "logquota",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Microtokens = table.Column<long>(type: "bigint", nullable: false),
                    LastRefill = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PolicyVersion = table.Column<byte[]>(type: "varbinary(8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TraceBuckets", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_TraceQuotaBucket_Key", "([SourceId] = '00000000-0000-0000-0000-000000000000' AND [ServiceId] = '00000000-0000-0000-0000-000000000000') OR ([SourceId] <> '00000000-0000-0000-0000-000000000000' AND [ServiceId] <> '00000000-0000-0000-0000-000000000000')");
                    table.CheckConstraint("CK_TraceQuotaBucket_Tokens", "[Microtokens] >= 0");
                });

            migrationBuilder.CreateTable(
                name: "TracePolicies",
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
                    table.PrimaryKey("PK_TracePolicies", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_TraceQuotaPolicy_Limits", "[SourceRate] > 0 AND [SourceBurst] > 0 AND [GlobalRate] > 0 AND [GlobalBurst] > 0");
                    table.CheckConstraint("CK_TraceQuotaPolicy_Name", "[Name] = 'central'");
                });

            migrationBuilder.CreateIndex(
                name: "IX_TraceBuckets_SourceId_ServiceId",
                schema: "logquota",
                table: "TraceBuckets",
                columns: new[] { "SourceId", "ServiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TracePolicies_Name",
                schema: "logquota",
                table: "TracePolicies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TracePolicies_RowId",
                schema: "logquota",
                table: "TracePolicies",
                column: "RowId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [logquota].[TraceBuckets]) OR EXISTS (SELECT 1 FROM [logquota].[TracePolicies])
                    THROW 51008, 'Cannot remove trace admission while quota state exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "TraceBuckets",
                schema: "logquota");

            migrationBuilder.DropTable(
                name: "TracePolicies",
                schema: "logquota");
        }
    }
}
