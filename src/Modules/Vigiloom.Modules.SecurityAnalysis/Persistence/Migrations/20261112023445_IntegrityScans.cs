using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vigiloom.Modules.SecurityAnalysis.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrityScans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrityChanges",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CoverageGap = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityChanges", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_IntegrityChanges_Content", "([Kind] = 1 AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 2 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");
                    table.CheckConstraint("CK_IntegrityChanges_Values", "[ScopeRevision] > 0 AND [Generation] > 0 AND [Kind] BETWEEN 1 AND 3");
                });

            migrationBuilder.CreateTable(
                name: "IntegrityScopeStates",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClosedGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ActiveGeneration = table.Column<long>(type: "bigint", nullable: true),
                    CurrentGeneration = table.Column<long>(type: "bigint", nullable: true),
                    LastAttemptGeneration = table.Column<long>(type: "bigint", nullable: true),
                    LastSnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastOutcome = table.Column<int>(type: "int", nullable: true),
                    LastStatus = table.Column<int>(type: "int", nullable: true),
                    LastPartialReason = table.Column<int>(type: "int", nullable: true),
                    LastCoverageGap = table.Column<bool>(type: "bit", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastCompleteAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityScopeStates", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_IntegrityScopeStates_AgentId_ScopeId", x => new { x.AgentId, x.ScopeId })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntegrityScopeStates_Generations", "[ClosedGeneration] >= 0 AND ([ActiveGeneration] IS NULL OR [ActiveGeneration] > [ClosedGeneration]) AND ([CurrentGeneration] IS NULL OR ([CurrentGeneration] > 0 AND [CurrentGeneration] <= [ClosedGeneration]))");
                    table.CheckConstraint("CK_IntegrityScopeStates_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [ScopeId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceGeneration] >= 0");
                    table.ForeignKey(
                        name: "FK_IntegrityScopeStates_IntegrityScopes_ScopeId",
                        column: x => x.ScopeId,
                        principalSchema: "security",
                        principalTable: "IntegrityScopes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "IntegrityScans",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeRevision = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    CoverageGap = table.Column<bool>(type: "bit", nullable: false),
                    PartialReason = table.Column<int>(type: "int", nullable: true),
                    ChunkCount = table.Column<int>(type: "int", nullable: false),
                    TotalItems = table.Column<int>(type: "int", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ReceivedChunks = table.Column<int>(type: "int", nullable: false),
                    ReceivedItems = table.Column<int>(type: "int", nullable: false),
                    ReceivedBytes = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityScans", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.UniqueConstraint("AK_IntegrityScans_AgentId_ScopeId_Generation", x => new { x.AgentId, x.ScopeId, x.Generation })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_IntegrityScans_Complete", "[Status] <> 2 OR ([Outcome] = 1 AND [ReceivedChunks] = [ChunkCount] AND [ReceivedItems] = [TotalItems] AND [ReceivedBytes] = [TotalBytes])");
                    table.CheckConstraint("CK_IntegrityScans_Limits", "[ChunkCount] BETWEEN 1 AND 1024 AND [TotalItems] BETWEEN 0 AND 20000 AND [TotalBytes] BETWEEN 2 AND 33554432 AND [ReceivedChunks] BETWEEN 0 AND [ChunkCount] AND [ReceivedItems] BETWEEN 0 AND [TotalItems] AND [ReceivedBytes] BETWEEN 0 AND [TotalBytes]");
                    table.CheckConstraint("CK_IntegrityScans_Terminal", "([Status] = 1 AND [FinishedAt] IS NULL) OR ([Status] <> 1 AND [FinishedAt] IS NOT NULL)");
                    table.CheckConstraint("CK_IntegrityScans_Values", "[Generation] > 0 AND [ScopeRevision] > 0 AND [DeviceGeneration] >= 0 AND [Outcome] BETWEEN 1 AND 5 AND [Status] BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_IntegrityScans_IntegrityScopeStates_AgentId_ScopeId",
                        columns: x => new { x.AgentId, x.ScopeId },
                        principalSchema: "security",
                        principalTable: "IntegrityScopeStates",
                        principalColumns: new[] { "AgentId", "ScopeId" });
                });

            migrationBuilder.CreateTable(
                name: "IntegrityChunks",
                schema: "security",
                columns: table => new
                {
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Generation = table.Column<long>(type: "bigint", nullable: false),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    Payload = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrityChunks", x => x.RowId)
                        .Annotation("SqlServer:Clustered", true);
                    table.CheckConstraint("CK_IntegrityChunks_Values", "[Generation] > 0 AND [ChunkIndex] BETWEEN 0 AND 1023 AND DATALENGTH([Payload]) BETWEEN 1 AND 1048576");
                    table.ForeignKey(
                        name: "FK_IntegrityChunks_IntegrityScans_AgentId_ScopeId_Generation",
                        columns: x => new { x.AgentId, x.ScopeId, x.Generation },
                        principalSchema: "security",
                        principalTable: "IntegrityScans",
                        principalColumns: new[] { "AgentId", "ScopeId", "Generation" });
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChanges_AgentId_ScopeId_ObservedAt_RowId",
                schema: "security",
                table: "IntegrityChanges",
                columns: new[] { "AgentId", "ScopeId", "ObservedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChanges_DeviceId_ObservedAt_RowId",
                schema: "security",
                table: "IntegrityChanges",
                columns: new[] { "DeviceId", "ObservedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChanges_RecordedAt_RowId",
                schema: "security",
                table: "IntegrityChanges",
                columns: new[] { "RecordedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityChunks_AgentId_ScopeId_Generation_ChunkIndex",
                schema: "security",
                table: "IntegrityChunks",
                columns: new[] { "AgentId", "ScopeId", "Generation", "ChunkIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScans_AgentId_SnapshotId",
                schema: "security",
                table: "IntegrityScans",
                columns: new[] { "AgentId", "SnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScans_Status_StartedAt_RowId",
                schema: "security",
                table: "IntegrityScans",
                columns: new[] { "Status", "StartedAt", "RowId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopeStates_DeviceId_ScopeId",
                schema: "security",
                table: "IntegrityScopeStates",
                columns: new[] { "DeviceId", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrityScopeStates_ScopeId",
                schema: "security",
                table: "IntegrityScopeStates",
                column: "ScopeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM [security].[IntegrityScopeStates] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[IntegrityScans] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[IntegrityChunks] WITH (TABLOCKX, HOLDLOCK))
                   OR EXISTS (SELECT 1 FROM [security].[IntegrityChanges] WITH (TABLOCKX, HOLDLOCK))
                    THROW 51000, 'Cannot downgrade while integrity scan data exists.', 1;
                """);
            migrationBuilder.DropTable(
                name: "IntegrityChanges",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntegrityChunks",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntegrityScans",
                schema: "security");

            migrationBuilder.DropTable(
                name: "IntegrityScopeStates",
                schema: "security");
        }
    }
}
