using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.AgentManagement.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentBatchReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReceivedBatches",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ItemCount = table.Column<int>(type: "int", nullable: false),
                    ByteCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedBatches", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_ReceivedBatches_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RuntimeObservations",
                schema: "agent",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OperatingSystem = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Architecture = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RuntimeVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeObservations", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.ForeignKey(
                        name: "FK_RuntimeObservations_Agents_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "agent",
                        principalTable: "Agents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedBatches_AgentId_BatchId",
                schema: "agent",
                table: "ReceivedBatches",
                columns: new[] { "AgentId", "BatchId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedBatches_AgentId_ReceivedAt_RowId",
                schema: "agent",
                table: "ReceivedBatches",
                columns: new[] { "AgentId", "ReceivedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeObservations_AgentId_MessageId",
                schema: "agent",
                table: "RuntimeObservations",
                columns: new[] { "AgentId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuntimeObservations_AgentId_OccurredAt_RowId",
                schema: "agent",
                table: "RuntimeObservations",
                columns: new[] { "AgentId", "OccurredAt", "RowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceivedBatches",
                schema: "agent");

            migrationBuilder.DropTable(
                name: "RuntimeObservations",
                schema: "agent");
        }
    }
}
