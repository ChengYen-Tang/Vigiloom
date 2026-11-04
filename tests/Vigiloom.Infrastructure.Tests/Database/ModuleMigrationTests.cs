using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Contracts.Devices;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Migrator.Installation;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ModuleMigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Migrations_CreateExpectedKeysIndexesAndForeignKeys() => WithDatabase(async (connectionString, token) =>
    {
        await using var resources = Resources(connectionString);
        await using var network = Network(connectionString);
        await resources.Database.MigrateAsync(token);
        await network.Database.MigrateAsync(token);
        // Applying the same migration sequence again must be safe.
        await resources.Database.MigrateAsync(token);
        await network.Database.MigrateAsync(token);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'resources' AND t.name IN ('Devices', 'Groups')
              AND ((i.is_primary_key = 1 AND i.type_desc = 'NONCLUSTERED')
                OR (i.is_unique = 1 AND i.type_desc = 'CLUSTERED'))
            """;
        Assert.AreEqual(4, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = """
            SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'resources' AND t.name IN ('Devices', 'Groups')
              AND c.name = 'RowId' AND c.system_type_id = TYPE_ID('bigint') AND c.is_identity = 1
            """;
        Assert.AreEqual(2, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE i.is_primary_key = 1 AND i.type_desc = 'CLUSTERED'
              AND ((s.name = 'resources' AND t.name IN ('DeviceGroups', 'ServiceGroups', 'DeviceTargets', 'DeviceAudits')) OR (s.name = 'network' AND t.name = 'PingSamples'))
            """;
        Assert.AreEqual(5, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = """
            SELECT COUNT(*) FROM sys.foreign_keys f
            JOIN sys.tables t ON t.object_id = f.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE (s.name = 'resources' AND t.name IN ('DeviceGroups', 'ServiceGroups', 'DeviceTargets', 'DeviceAudits') OR s.name = 'network' AND t.name = 'PingSamples')
              AND f.delete_referential_action_desc = 'NO_ACTION'
            """;
        Assert.AreEqual(6, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = "SELECT COUNT(*) FROM resources.__EFMigrationsHistory";
        Assert.AreEqual(6, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = 'resources' AND t.name = 'DeviceAudits' AND i.name = 'IX_DeviceAudits_OccurredAt_RowId'
              AND ((ic.key_ordinal = 1 AND c.name = 'OccurredAt') OR (ic.key_ordinal = 2 AND c.name = 'RowId'))
            """;
        Assert.AreEqual(2, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = "SELECT COUNT(*) FROM network.__EFMigrationsHistory";
        Assert.AreEqual(5, (int)(await command.ExecuteScalarAsync(token))!);
    });

    [TestMethod]
    public Task ResourceIds_KeysAndRelationships_AreEnforcedBySqlServer() => WithDatabase(async (connectionString, token) =>
    {
        await using var resources = Resources(connectionString);
        await using var network = Network(connectionString);
        await resources.Database.MigrateAsync(token);
        await network.Database.MigrateAsync(token);
        var now = DateTimeOffset.UtcNow;
        var device = new Device("Router", "example.test", false, now);
        var group = new Group("Networks", now);
        Assert.AreEqual(7, device.Id.Version);
        var id = device.Id;
        resources.Devices.Add(device);
        resources.Groups.Add(group);
        resources.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
        await resources.SaveChangesAsync(token);
        Assert.AreEqual(id, device.Id);
        Assert.AreNotEqual(0L, device.RowId);
        resources.ChangeTracker.Clear();
        resources.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
        await Assert.ThrowsAsync<DbUpdateException>(async () => await resources.SaveChangesAsync(token));
        resources.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqlException>(async () => await resources.Database.ExecuteSqlRawAsync(
            "INSERT INTO resources.Devices (Id, Name, Address, HasAgent, IsArchived, CreatedAt, UpdatedAt) VALUES ('00000000-0000-0000-0000-000000000000', 'x', 'x', 0, 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())", token));
        network.PingSamples.Add(new PingSample(id, "central", now, now, true, 1.5));
        await network.SaveChangesAsync(token);
        network.PingSamples.Add(new PingSample(Guid.CreateVersion7(), "central", now, now, false, null));
        await Assert.ThrowsAsync<DbUpdateException>(async () => await network.SaveChangesAsync(token));
    });

    [TestMethod]
    public Task ConcurrencyAndTransaction_RetainIdentityAndRollBack() => WithDatabase(async (connectionString, token) =>
    {
        await using var first = Resources(connectionString);
        await first.Database.MigrateAsync(token);
        var device = new Device("Original", "example.test", false, DateTimeOffset.UtcNow);
        var id = device.Id;
        first.Devices.Add(device);
        await first.SaveChangesAsync(token);
        await using var second = Resources(connectionString);
        var stale = await second.Devices.SingleAsync(x => x.Id == id, token);
        device.Rename("Current", DateTimeOffset.UtcNow);
        await first.SaveChangesAsync(token);
        stale.Rename("Stale", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await second.SaveChangesAsync(token));
        Assert.AreEqual(id, stale.Id);
        await using (var transaction = await first.Database.BeginTransactionAsync(token))
        {
            first.Groups.Add(new Group("Rolled back", DateTimeOffset.UtcNow));
            await first.SaveChangesAsync(token);
            await transaction.RollbackAsync(token);
        }
        first.ChangeTracker.Clear();
        Assert.AreEqual(0, await first.Groups.CountAsync(token));
        Assert.AreEqual(id, (await first.Devices.SingleAsync(token)).Id);
    });

    [TestMethod]
    public Task DeviceManagement_PersistsTargetsAuditsAndEnforcesConcurrency() => WithDatabase(async (connectionString, token) =>
    {
        var services = new ServiceCollection();
        services.AddModuleSqlServer<ResourcesDbContext>(connectionString, "resources");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        var service = new DeviceManagement(factory, TimeProvider.System);
        var actor = Guid.CreateVersion7();
        var input = new DeviceInput("Router", " EXAMPLE.COM. ", ["2001:0DB8::1"], false);
        var created = await service.CreateAsync(input, actor, token);
        Assert.AreEqual(DeviceResultStatus.Success, created.Status);
        Assert.AreEqual(7, created.Device!.Id.Version);
        Assert.AreEqual("example.com", created.Device.Address);
        Assert.AreEqual("2001:db8::1", created.Device.AdditionalTargets.Single());
        var duplicate = await service.CreateAsync(input with { AdditionalTargets = ["EXAMPLE.com"] }, actor, token);
        Assert.AreEqual(DeviceResultStatus.Invalid, duplicate.Status);
        var blockedPublication = await service.CreateAsync(input with { Public = true }, actor, token);
        Assert.AreEqual(DeviceResultStatus.Invalid, blockedPublication.Status);
        var disabled = await service.CreateAsync(input with { Name = "Disabled", Enabled = false }, actor, token);
        Assert.AreEqual(DeviceResultStatus.Success, disabled.Status);
        Assert.IsFalse((await service.GetAsync(disabled.Device!.Id, token))!.Enabled);
        var matches = await service.FindAddressMatchesAsync(["2001:0db8::1", "EXAMPLE.COM"], created.Device.Id, token);
        Assert.HasCount(1, matches);
        Assert.AreEqual(disabled.Device.Id, matches[0].Id);
        var updated = await service.UpdateAsync(created.Device.Id, input with { Name = "Router 2", Version = created.Device.Version }, actor, token);
        Assert.AreEqual(DeviceResultStatus.Success, updated.Status);
        Assert.AreEqual(created.Device.Id, updated.Device!.Id);
        var stale = await service.UpdateAsync(created.Device.Id, input with { Version = created.Device.Version }, actor, token);
        Assert.AreEqual(DeviceResultStatus.Conflict, stale.Status);
        var archived = await service.SetArchivedAsync(created.Device.Id, true, updated.Device.Version, actor, token);
        Assert.AreEqual(DeviceResultStatus.Success, archived.Status);
        Assert.IsTrue(archived.Device!.Archived);
        Assert.IsFalse(archived.Device.Enabled);
        Assert.AreEqual(1, (await service.ListAsync(null, null, false, false, 1, token)).Total);
        var restored = await service.SetArchivedAsync(created.Device.Id, false, archived.Device.Version, actor, token);
        Assert.AreEqual(DeviceResultStatus.Success, restored.Status);
        Assert.IsFalse(restored.Device!.Enabled);
        Assert.AreEqual(created.Device.Id, restored.Device.Id);
        await using var verify = await factory.CreateDbContextAsync(token);
        Assert.AreEqual(5, await verify.DeviceAudits.CountAsync(token));
        Assert.AreEqual(2, await verify.DeviceTargets.CountAsync(token));
    });

    [TestMethod]
    public Task Migrator_DeploysAndCanBeRunAgain() => WithDatabase(async (connectionString, token) =>
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RunMigratorAsync(connectionString, null, token);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.Contains("completed", result.Output);
        }
        await using var db = Resources(connectionString);
        Assert.IsFalse((await db.Database.GetPendingMigrationsAsync(token)).Any());
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'operations.AuditEvents', N'U') IS NOT NULL THEN 1 ELSE 0 END AS [Value]").SingleAsync(token));
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'service.Candidates', N'U') IS NOT NULL AND OBJECT_ID(N'service.Services', N'U') IS NOT NULL AND OBJECT_ID(N'service.Sources', N'U') IS NOT NULL THEN 1 ELSE 0 END AS [Value]").SingleAsync(token));
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'operations.AuditEvents') AND name = N'IX_AuditEvents_OccurredAt_RowId'").SingleAsync(token));
        await using var identity = new IdentityAccessDbContext(new DbContextOptionsBuilder<IdentityAccessDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);
        Assert.AreEqual(2, await identity.Roles.CountAsync(token));
    });

    [TestMethod]
    public Task Migrator_FreshInstallEnforcesDefaultAndRefusesExistingDatabase() => WithDatabase(async (connectionString, token) =>
    {
        var fresh = await RunMigratorAsync(connectionString, "fresh-install", token);
        Assert.AreEqual(0, fresh.ExitCode, fresh.Error);
        await using var db = Resources(connectionString);
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicies] WHERE [Phase] = 2 AND [FreshInstallation] = 1 AND [GraceUntil] IS NULL AND [ActivatedAt] = [EnforcedAt]").SingleAsync(token));
        var repeat = await RunMigratorAsync(connectionString, "fresh-install", token);
        Assert.AreEqual(0, repeat.ExitCode, repeat.Error);
        var normal = await RunMigratorAsync(connectionString, null, token);
        Assert.AreEqual(0, normal.ExitCode, normal.Error);
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicies] WHERE [Phase] = 2 AND [FreshInstallation] = 1").SingleAsync(token));
    });

    [TestMethod]
    public Task Migrator_DetectsTerminatedDeploymentLock() => WithDatabase(async (connectionString, token) =>
    {
        await using var lease = await InstallLease.AcquireAsync(connectionString);
        await lease.VerifyAsync();
        await using (var db = new SqlConnection(connectionString))
        {
            await db.OpenAsync(token);
            await using var command = db.CreateCommand();
            var session = lease.SessionId;
            Assert.IsGreaterThan(0, session);
            command.CommandText = $"KILL {session}";
            await command.ExecuteNonQueryAsync(token);
        }
        await Assert.ThrowsAsync<SqlException>(() => lease.VerifyAsync());
    });

    [TestMethod]
    public Task Migrator_ResumesPendingFreshInstallWithoutAllowingUpgrade() => WithDatabase(async (connectionString, token) =>
    {
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE dbo.VigiloomInstallIntent (Id uniqueidentifier NOT NULL PRIMARY KEY, FormatVersion int NOT NULL, CreatedAt datetimeoffset NOT NULL, Completed bit NOT NULL); INSERT INTO dbo.VigiloomInstallIntent VALUES (@id, 1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0)";
            command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            await command.ExecuteNonQueryAsync(token);
        }
        var upgrade = await RunMigratorAsync(connectionString, null, token);
        Assert.AreNotEqual(0, upgrade.ExitCode);
        var resumed = await RunMigratorAsync(connectionString, "fresh-install", token);
        Assert.AreEqual(0, resumed.ExitCode, resumed.Error);
        await using var db = Resources(connectionString);
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicies] WHERE [Phase] = 2 AND [FreshInstallation] = 1").SingleAsync(token));
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.VigiloomInstallIntent WHERE [Completed] = 1").SingleAsync(token));
    });

    [TestMethod]
    public Task Migrator_FreshCompletionRejectsConcurrentAgentRegistration() => WithDatabase(async (connectionString, token) =>
    {
        var deployed = await RunMigratorAsync(connectionString, null, token);
        Assert.AreEqual(0, deployed.ExitCode, deployed.Error);
        var device = new Device("concurrent agent", "127.0.0.1", true, DateTimeOffset.UtcNow);
        await using (var resources = Resources(connectionString)) { resources.Devices.Add(device); await resources.SaveChangesAsync(token); }
        await using var lease = await InstallLease.AcquireAsync(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE dbo.VigiloomInstallIntent (Id uniqueidentifier NOT NULL PRIMARY KEY, FormatVersion int NOT NULL, CreatedAt datetimeoffset NOT NULL, Completed bit NOT NULL); INSERT INTO dbo.VigiloomInstallIntent VALUES (@id, 1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0)";
            create.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            await create.ExecuteNonQueryAsync(token);
        }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO [agent].[Agents] (Id, DeviceId, DeviceGeneration, Verifier, CreatedAt, HeartbeatSafetyIntervalSeconds) VALUES (@id, @device, 0, @verifier, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 30)";
            insert.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("@device", device.Id);
            insert.Parameters.AddWithValue("@verifier", new byte[32]);
            await insert.ExecuteNonQueryAsync(token);
        }
        var completion = lease.CompleteFreshAsync();
        Exception? observationFailure = null;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            Assert.IsFalse(completion.IsCompleted, "Fresh completion must wait for the concurrent Agent transaction.");
        }
        catch (Exception ex) { observationFailure = ex; }
        Exception? completionFailure = null;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await transaction.CommitAsync(cleanup.Token);
            var failure = await Assert.ThrowsAsync<SqlException>(() => completion.WaitAsync(cleanup.Token));
            Assert.AreEqual(51000, failure.Number);
        }
        catch (Exception ex) { completionFailure = ex; }
        if (observationFailure is not null && completionFailure is not null) throw new AggregateException(observationFailure, completionFailure);
        if (observationFailure is not null) ExceptionDispatchInfo.Capture(observationFailure).Throw();
        if (completionFailure is not null) ExceptionDispatchInfo.Capture(completionFailure).Throw();
        await using var verify = Resources(connectionString);
        Assert.AreEqual(1, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicies] WHERE [Phase] = 0").SingleAsync(token));
        Assert.AreEqual(1, await verify.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM dbo.VigiloomInstallIntent WHERE [Completed] = 0").SingleAsync(token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Migrator_FreshCompletionFencesNewAgentUntilCommit(bool snapshotReads) => WithDatabase(async (connectionString, token) =>
    {
        var deployed = await RunMigratorAsync(connectionString, null, token);
        Assert.AreEqual(0, deployed.ExitCode, deployed.Error);
        await using var db = Resources(connectionString);
        if (snapshotReads) await db.Database.ExecuteSqlRawAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", token);
        var device = new Device("fence probe", "127.0.0.1", true, DateTimeOffset.UtcNow);
        db.Devices.Add(device); await db.SaveChangesAsync(token);
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE dbo.VigiloomInstallIntent (Id uniqueidentifier NOT NULL PRIMARY KEY, FormatVersion int NOT NULL, CreatedAt datetimeoffset NOT NULL, Completed bit NOT NULL); CREATE TABLE dbo.ReplayTestGate (Id int PRIMARY KEY); INSERT dbo.ReplayTestGate VALUES (1)", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT dbo.VigiloomInstallIntent VALUES ({Guid.CreateVersion7()}, 1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 0)", token);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [agent].[ReplayTestPause] ON [agent].[ReplayPolicies] AFTER UPDATE AS BEGIN RAISERROR ('REPLAY_TEST_GATE', 0, 1) WITH NOWAIT; DECLARE @id int; SELECT @id = Id FROM dbo.ReplayTestGate WITH (UPDLOCK, HOLDLOCK) WHERE Id = 1; END", token);
        await using var gate = new SqlConnection(connectionString);
        await gate.OpenAsync(token);
        await using var gateTransaction = (SqlTransaction)await gate.BeginTransactionAsync(token);
        await using (var hold = gate.CreateCommand())
        {
            hold.Transaction = gateTransaction;
            hold.CommandText = "UPDATE dbo.ReplayTestGate SET Id = Id WHERE Id = 1";
            await hold.ExecuteNonQueryAsync(token);
        }
        await using var leaseConnection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0 }.ConnectionString);
        await leaseConnection.OpenAsync(token);
        await using (var getLock = leaseConnection.CreateCommand())
        {
            getLock.CommandText = "DECLARE @r int; EXEC @r = sys.sp_getapplock @Resource = N'Vigiloom.Migrator', @LockMode = 'Exclusive', @LockOwner = 'Session'; SELECT @r";
            Assert.IsGreaterThanOrEqualTo(0, Convert.ToInt32(await getLock.ExecuteScalarAsync(token)));
        }
        await using var lease = new InstallLease(leaseConnection);
        var completion = lease.CompleteFreshAsync();
        Exception? failure = null;
        try
        {
            await using (var observer = new SqlConnection(connectionString))
            {
                await observer.OpenAsync(token);
                await using var wait = observer.CreateCommand();
                wait.CommandText = "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') = 1 OR HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER PERFORMANCE STATE') = 1 THEN 1 ELSE 0 END";
                Assert.AreEqual(1, Convert.ToInt32(await wait.ExecuteScalarAsync(token)), "The isolated test account needs permission to observe SQL sessions; see tests/Vigiloom.Testing.SqlServer/README.md.");
                wait.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = @session AND blocking_session_id = @gate";
                wait.Parameters.AddWithValue("@session", leaseConnection.ServerProcessId);
                wait.Parameters.AddWithValue("@gate", gate.ServerProcessId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                while (Convert.ToInt32(await wait.ExecuteScalarAsync(deadline.Token)) == 0)
                {
                    if (completion.IsCompleted) await completion;
                    await Task.Delay(50, deadline.Token);
                }
            }
            await using var probe = new SqlConnection(connectionString);
            await probe.OpenAsync(token);
            await using var insert = probe.CreateCommand();
            insert.CommandText = "SET LOCK_TIMEOUT 0; INSERT INTO [agent].[Agents] (Id, DeviceId, DeviceGeneration, Verifier, CreatedAt, HeartbeatSafetyIntervalSeconds) VALUES (@id, @device, 0, @verifier, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 30)";
            insert.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            insert.Parameters.AddWithValue("@device", device.Id);
            insert.Parameters.AddWithValue("@verifier", new byte[32]);
            var blocked = await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync(token));
            Assert.AreEqual(1222, blocked.Number);
            await gateTransaction.CommitAsync(token);
            await completion.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.AreEqual(1, await insert.ExecuteNonQueryAsync(token));
        }
        catch (Exception ex) { failure = ex; }
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (gateTransaction.Connection is not null) await gateTransaction.RollbackAsync(cleanup.Token);
            await completion.WaitAsync(cleanup.Token);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [agent].[ReplayTestPause]", cleanup.Token);
        }
        catch (Exception cleanupFailure)
        {
            if (failure is not null) throw new AggregateException(failure, cleanupFailure);
            throw;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    });


    private static async Task<(int ExitCode, string Output, string Error)> RunMigratorAsync(string connectionString, string? mode, CancellationToken token)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vigiloom.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var project = Path.Combine(directory.FullName, "src", "Hosts", "Vigiloom.Migrator", "Vigiloom.Migrator.csproj");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "run", "--no-build", "--configuration", configuration, "--project", project }) start.ArgumentList.Add(argument);
        if (mode is not null) { start.ArgumentList.Add("--"); start.ArgumentList.Add(mode); }
        start.Environment["VIGILOOM_MIGRATION_SQL_CONNECTION"] = connectionString;
        using var process = Process.Start(start)!;
        Exception? failure = null;
        (int ExitCode, string Output, string Error) result = default;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            result = (process.ExitCode, await output, await error);
        }
        catch (Exception exception) { failure = exception; }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (Exception cleanupFailure)
        {
            if (failure is not null) throw new AggregateException(failure, cleanupFailure);
            throw;
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    [TestMethod]
    public Task IdentityRoles_InitializeConcurrentlyAndKeepIdsOnRestart() => WithDatabase(async (connectionString, token) =>
    {
        var services = new ServiceCollection();
        services.AddModuleSqlServer<IdentityAccessDbContext>(connectionString, "identity");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await Task.WhenAll(RoleInitializer.InitializeAsync(factory, token), RoleInitializer.InitializeAsync(factory, token));
        Dictionary<string, Guid> initial;
        await using (var db = await factory.CreateDbContextAsync(token))
            initial = await db.Roles.ToDictionaryAsync(x => x.NormalizedName!, x => x.Id, token);
        Assert.HasCount(2, initial);
        Assert.AreEqual(7, initial["ADMINISTRATOR"].Version);
        Assert.AreEqual(7, initial["READ ONLY"].Version);
        await RoleInitializer.InitializeAsync(factory, token);
        await using var check = await factory.CreateDbContextAsync(token);
        var afterRestart = await check.Roles.ToDictionaryAsync(x => x.NormalizedName!, x => x.Id, token);
        CollectionAssert.AreEquivalent(initial.ToArray(), afterRestart.ToArray());
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'identity' AND t.name IN ('AspNetUsers', 'AspNetRoles')
              AND ((i.is_primary_key = 1 AND i.type_desc = 'NONCLUSTERED')
                OR (i.is_unique = 1 AND i.type_desc = 'CLUSTERED'))
            """;
        Assert.AreEqual(4, (int)(await command.ExecuteScalarAsync(token))!);
    });

    [TestMethod]
    public Task RoleInitialization_TransactionRetryRetainsAssignedIds() => WithDatabase(async (connectionString, token) =>
    {
        var interceptor = new FailFirstRoleInsert();
        var options = new DbContextOptionsBuilder<IdentityAccessDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "identity")
                .ExecutionStrategy(dependencies => new RetryTestStrategy(dependencies)))
            .AddInterceptors(interceptor).Options;
        IDbContextFactory<IdentityAccessDbContext> factory = new IdentityFactory(options);
        await using (var db = await factory.CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await RoleInitializer.InitializeAsync(factory, token);
        await using var check = await factory.CreateDbContextAsync(token);
        var stored = await check.Roles.Select(x => x.Id).ToArrayAsync(token);
        Assert.IsTrue(interceptor.Failed);
        Assert.HasCount(2, interceptor.Ids);
        CollectionAssert.AreEquivalent(interceptor.Ids.ToArray(), stored);
    });

    private sealed class IdentityFactory(DbContextOptions<IdentityAccessDbContext> options) : IDbContextFactory<IdentityAccessDbContext>
    {
        public IdentityAccessDbContext CreateDbContext() => new(options);
    }

    private sealed class RetryTestStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, 1, TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is SimulatedTransientException;
    }

    private sealed class SimulatedTransientException : Exception;

    private sealed class FailFirstRoleInsert : DbCommandInterceptor
    {
        public bool Failed { get; private set; }
        public HashSet<Guid> Ids { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Failed && command.CommandText.Contains("[identity].[AspNetRoles]", StringComparison.Ordinal) && (command.CommandText.Contains("INSERT", StringComparison.Ordinal) || command.CommandText.Contains("MERGE", StringComparison.Ordinal)))
            {
                foreach (DbParameter parameter in command.Parameters)
                    if (parameter.Value is Guid id) Ids.Add(id);
                Failed = true;
                throw new SimulatedTransientException();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private Task WithDatabase(Func<string, CancellationToken, Task> test) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(),
            async (db, token) =>
            {
                await using (var connection = new SqlConnection(db.ConnectionString))
                {
                    await connection.OpenAsync(token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150";
                    await command.ExecuteNonQueryAsync(token);
                    command.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
                    Assert.AreEqual((byte)SqlServerRegistration.CompatibilityLevel, (byte)(await command.ExecuteScalarAsync(token))!);
                }
                await test(db.ConnectionString, token);
            }, TestContext.CancellationToken);

    private static ResourcesDbContext Resources(string connectionString) => new(
        new DbContextOptionsBuilder<ResourcesDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options);

    private static NetworkDbContext Network(string connectionString) => new(
        new DbContextOptionsBuilder<NetworkDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options);
}
