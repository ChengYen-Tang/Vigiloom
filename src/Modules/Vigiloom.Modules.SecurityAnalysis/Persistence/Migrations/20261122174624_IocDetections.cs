using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IocDetections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IocDetections",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Value = table.Column<string>(type: "varchar(253)", unicode: false, maxLength: 253, nullable: false),
                    Field = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    SourceLine = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IocDetections", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_IocDetections_IntelligenceRevisions_SourceId_RevisionId",
                        columns: x => new { x.SourceId, x.RevisionId },
                        principalSchema: "security",
                        principalTable: "IntelligenceRevisions",
                        principalColumns: new[] { "SourceId", "Id" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_IocDetections_AgentId_MessageId_RevisionId_Kind_SourceLine_Field",
                schema: "security",
                table: "IocDetections",
                columns: new[] { "AgentId", "MessageId", "RevisionId", "Kind", "SourceLine", "Field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IocDetections_DeviceId_ReceivedAt_RowId",
                schema: "security",
                table: "IocDetections",
                columns: new[] { "DeviceId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IocDetections_SourceId_RevisionId",
                schema: "security",
                table: "IocDetections",
                columns: new[] { "SourceId", "RevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IocDetections",
                schema: "security");
        }
    }
}
