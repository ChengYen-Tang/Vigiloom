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
              AND ((s.name = 'resources' AND t.name IN ('DeviceGroups', 'DeviceTargets', 'DeviceAudits')) OR (s.name = 'network' AND t.name = 'PingSamples'))
            """;
        Assert.AreEqual(4, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = """
            SELECT COUNT(*) FROM sys.foreign_keys f
            JOIN sys.tables t ON t.object_id = f.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE (s.name = 'resources' AND t.name IN ('DeviceGroups', 'DeviceTargets', 'DeviceAudits') OR s.name = 'network' AND t.name = 'PingSamples')
              AND f.delete_referential_action_desc = 'NO_ACTION'
            """;
        Assert.AreEqual(5, (int)(await command.ExecuteScalarAsync(token))!);
        command.CommandText = "SELECT COUNT(*) FROM resources.__EFMigrationsHistory";
        Assert.AreEqual(5, (int)(await command.ExecuteScalarAsync(token))!);
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
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vigiloom.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var project = Path.Combine(directory.FullName, "src", "Hosts", "Vigiloom.Migrator", "Vigiloom.Migrator.csproj");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "run", "--no-build", "--configuration", configuration, "--project", project }) start.ArgumentList.Add(argument);
            start.Environment["VIGILOOM_MIGRATION_SQL_CONNECTION"] = connectionString;
            using var process = Process.Start(start)!;
            Exception? failure = null;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(token);
                var error = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                Assert.AreEqual(0, process.ExitCode, await error);
                Assert.Contains("completed", await output);
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
        }
        await using var db = Resources(connectionString);
        Assert.IsFalse((await db.Database.GetPendingMigrationsAsync(token)).Any());
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID(N'operations.AuditEvents', N'U') IS NOT NULL THEN 1 ELSE 0 END AS [Value]").SingleAsync(token));
        Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id = OBJECT_ID(N'operations.AuditEvents') AND name = N'IX_AuditEvents_OccurredAt_RowId'").SingleAsync(token));
        await using var identity = new IdentityAccessDbContext(new DbContextOptionsBuilder<IdentityAccessDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "identity")).Options);
        Assert.AreEqual(2, await identity.Roles.CountAsync(token));
    });

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
