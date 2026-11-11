using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InventorySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "security");

            migrationBuilder.CreateTable(
                name: "InventorySources",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    ClosedGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ActiveGeneration = table.Column<long>(type: "bigint", nullable: true),
                    CurrentGeneration = table.Column<long>(type: "bigint", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompleteAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAttemptGeneration = table.Column<long>(type: "bigint", nullable: true),
                    LastOutcome = table.Column<int>(type: "int", nullable: true),
                    LastStatus = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySources", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_InventorySources_AgentId_Source", x => new { x.AgentId, x.Source })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_InventorySources_DeviceGeneration", "[DeviceGeneration] >= 0");
                    table.CheckConstraint("CK_InventorySources_Generations", "[ClosedGeneration] >= 0 AND ([ActiveGeneration] IS NULL OR [ActiveGeneration] > [ClosedGeneration]) AND ([CurrentGeneration] IS NULL OR ([CurrentGeneration] > 0 AND [CurrentGeneration] <= [ClosedGeneration]))");
                    table.CheckConstraint("CK_InventorySources_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_InventorySources_LastAttempt", "([LastAttemptGeneration] IS NULL AND [LastAttemptAt] IS NULL AND [LastOutcome] IS NULL AND [LastStatus] IS NULL) OR ([LastAttemptGeneration] IS NOT NULL AND [LastAttemptGeneration] > 0 AND [LastAttemptAt] IS NOT NULL AND [LastOutcome] IS NOT NULL AND [LastOutcome] BETWEEN 1 AND 5 AND [LastStatus] IS NOT NULL AND [LastStatus] BETWEEN 2 AND 5)");
                    table.CheckConstraint("CK_InventorySources_Source", "[Source] BETWEEN 1 AND 10");
                });

            migrationBuilder.CreateTable(
                name: "InventorySnapshots",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    TotalItems = table.Column<int>(type: "int", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ReceivedChunks = table.Column<int>(type: "int", nullable: false),
                    ReceivedItems = table.Column<int>(type: "int", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventorySnapshots", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_InventorySnapshots_AgentId_Source_Generation", x => new { x.AgentId, x.Source, x.Generation })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_InventorySnapshots_Complete", "[Status] <> 2 OR ([Outcome] = 1 AND [ReceivedChunks] = [ChunkCount] AND [ReceivedItems] = [TotalItems] AND [ReceivedBytes] = [TotalBytes])");
                    table.CheckConstraint("CK_InventorySnapshots_Generation", "[Generation] > 0 AND [DeviceGeneration] >= 0 AND [SchemaVersion] > 0");
                    table.CheckConstraint("CK_InventorySnapshots_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [SnapshotId] <> '00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_InventorySnapshots_Limits", "[ChunkCount] BETWEEN 1 AND 1024 AND [TotalItems] BETWEEN 0 AND 20000 AND [TotalBytes] BETWEEN 2 AND 33554432 AND [ReceivedChunks] BETWEEN 0 AND [ChunkCount] AND [ReceivedItems] BETWEEN 0 AND [TotalItems] AND [ReceivedBytes] BETWEEN 0 AND [TotalBytes]");
                    table.CheckConstraint("CK_InventorySnapshots_Partial", "[Status] <> 3 OR [Outcome] <> 1");
                    table.CheckConstraint("CK_InventorySnapshots_Status", "[Status] BETWEEN 1 AND 5 AND [Outcome] BETWEEN 1 AND 5 AND (([Status] = 1 AND [FinishedAt] IS NULL) OR ([Status] <> 1 AND [FinishedAt] IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_InventorySnapshots_InventorySources_AgentId_Source",
                        columns: x => new { x.AgentId, x.Source },
                        principalSchema: "security",
                        principalTable: "InventorySources",
                        principalColumns: new[] { "AgentId", "Source" });
                });

            migrationBuilder.CreateTable(
                name: "InventoryChunks",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryChunks", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_InventoryChunks_Index", "[Generation] > 0 AND [ChunkIndex] BETWEEN 0 AND 1023");
                    table.CheckConstraint("CK_InventoryChunks_Size", "DATALENGTH([Payload]) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_InventoryChunks_InventorySnapshots_AgentId_Source_Generation",
                        columns: x => new { x.AgentId, x.Source, x.Generation },
                        principalSchema: "security",
                        principalTable: "InventorySnapshots",
                        principalColumns: new[] { "AgentId", "Source", "Generation" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryChunks_AgentId_Source_Generation_ChunkIndex",
                schema: "security",
                table: "InventoryChunks",
                columns: new[] { "AgentId", "Source", "Generation", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventorySnapshots_AgentId_SnapshotId",
                schema: "security",
                table: "InventorySnapshots",
                columns: new[] { "AgentId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventorySnapshots_Status_StartedAt_RowId",
                schema: "security",
                table: "InventorySnapshots",
                columns: new[] { "Status", "StartedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventorySources_DeviceId_Source",
                schema: "security",
                table: "InventorySources",
                columns: new[] { "DeviceId", "Source" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A fence alone is durable replay protection; it cannot be dropped while any source exists.
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[InventorySources] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[InventorySnapshots] WITH (TABLOCKX, HOLDLOCK))
                    OR EXISTS (SELECT 1 FROM [security].[InventoryChunks] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Inventory data or generation fences prevent downgrade.', 1;
                """);
            migrationBuilder.DropTable(
                name: "InventoryChunks",
                schema: "security");

            migrationBuilder.DropTable(
                name: "InventorySnapshots",
                schema: "security");

            migrationBuilder.DropTable(
                name: "InventorySources",
                schema: "security");
        }
    }
}
