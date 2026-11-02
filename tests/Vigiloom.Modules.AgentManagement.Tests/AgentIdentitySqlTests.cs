using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentitySqlTests
{
    public TestContext TestContext { get; set; } = null!;
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task SchemaUsesResourceKeysClusteredRowsAndDeviceForeignKeys() => WithDatabase(async (_, agents, token) =>
    {
        await using var db = agents.CreateDbContext();
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND t.name IN ('Agents', 'Enrollments')
              AND ((i.is_primary_key = 1 AND i.type_desc = 'NONCLUSTERED') OR (i.is_unique = 1 AND i.type_desc = 'CLUSTERED'))
            """;
        Assert.AreEqual(4, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.foreign_keys f
            JOIN sys.tables t ON f.parent_object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND f.delete_referential_action_desc = 'NO_ACTION'
              AND f.name IN ('FK_Agents_Devices_DeviceId', 'FK_Enrollments_Devices_DeviceId', 'FK_Enrollments_Agents_AgentId',
                             'FK_AgentAudits_Devices_DeviceId', 'FK_AgentAudits_Agents_AgentId')
            """;
        Assert.AreEqual(5, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND t.name = 'Agents' AND i.name = 'IX_Agents_DeviceId' AND i.is_unique = 1 AND i.has_filter = 1
            """;
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EnrollmentRetriesAndArchiveNeverRevivesCredential(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var now = new TestClock();
        var device = new Device("server", "127.0.0.1", true, now.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), now);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
        var secret = RandomNumberGenerator.GetBytes(32);
        var verifier = SHA256.HashData(secret);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.RedeemAsync(issued.Secret, verifier, token)));
        Assert.IsTrue(attempts.All(x => x.Status == AgentIdentityStatus.Success));
        Assert.AreEqual(attempts[0].AgentId, attempts[1].AgentId);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        var credential = $"vla_{attempts[0].AgentId:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.IsNotNull(await service.AuthenticateAsync(credential, token));
        Assert.IsNull(await service.AuthenticateAsync(issued.Secret, token));
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Archive(now.GetUtcNow()); await db.SaveChangesAsync(token);
            current.Restore(now.GetUtcNow()); current.Update(current.Name, current.Address, true, true, now.GetUtcNow()); await db.SaveChangesAsync(token);
        }
        Assert.IsNull(await service.AuthenticateAsync(credential, token));
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, verifier, token)).Status);
        var replacement = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        Assert.AreEqual(AgentIdentityStatus.Success, replacement.Status);
        Assert.AreEqual(AgentIdentityStatus.Success, (await service.RedeemAsync(replacement.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(2, await check.Agents.CountAsync(token));
        Assert.AreEqual(1, await check.Agents.CountAsync(x => x.RevokedAt == null, token));
    }, snapshotReads);

    [TestMethod]
    public Task CompetingMachinesCannotRedeemSameEnrollment() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var results = await Task.WhenAll(service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token),
            service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token));
        Assert.AreEqual(1, results.Count(x => x.Status == AgentIdentityStatus.Success));
        Assert.AreEqual(1, results.Count(x => x.Status == AgentIdentityStatus.Invalid));
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(1, await check.Agents.CountAsync(token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ArchiveHoldingDeviceLockPreventsConcurrentEnrollment(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        await using var archive = resources.CreateDbContext();
        await using var transaction = await archive.Database.BeginTransactionAsync(token);
        var current = await archive.Devices.SingleAsync(x => x.Id == device.Id, token);
        current.Archive(clock.GetUtcNow());
        await archive.SaveChangesAsync(token);
        var redeem = service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token);
        // Verify the competing operation is actually waiting for SQL, rather than merely scheduled.
        await Task.Delay(250, token);
        Assert.IsFalse(redeem.IsCompleted, "Enrollment must wait until the archive transaction commits.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await redeem).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(0, await check.Agents.CountAsync(token));
    }, snapshotReads);

    [TestMethod]
    public Task RecoveryRequiresBothSecretsAndNeverCreatesARegistration() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var actor = Guid.CreateVersion7();
        var issued = await service.IssueAsync(device.Id, actor, token);
        var machine = RandomNumberGenerator.GetBytes(32);
        var proof = $"vlr_{issued.Secret!.Substring(4, 36)}.{Convert.ToBase64String(machine).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, proof, token)).Status);
        var enrolled = await service.RedeemAsync(issued.Secret, SHA256.HashData(machine), token);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, SHA256.HashData(machine), token)).Status);
        Assert.AreEqual(enrolled.AgentId, (await service.RecoverAsync(issued.Secret, proof, token)).AgentId);
        var wrong = RandomNumberGenerator.GetBytes(32);
        var wrongProof = $"vlr_{issued.Secret.Substring(4, 36)}.{Convert.ToBase64String(wrong).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, wrongProof, token)).Status);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(null, proof, token)).Status);
        await service.RevokeAsync(enrolled.AgentId!.Value, actor, token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, proof, token)).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(1, await check.Agents.CountAsync(token));
    });

    [TestMethod]
    public Task RotationSurvivesRetriesAndDoesNotActivatePendingCredentialEarly() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var oldSecret = RandomNumberGenerator.GetBytes(32);
        var enrolled = await service.RedeemAsync(issued.Secret, SHA256.HashData(oldSecret), token);
        var newSecret = RandomNumberGenerator.GetBytes(32);
        var oldCredential = Credential(enrolled.AgentId!.Value, oldSecret);
        var newCredential = Credential(enrolled.AgentId.Value, newSecret);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(oldCredential, SHA256.HashData(newSecret), token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(oldCredential, SHA256.HashData(newSecret), token));
        Assert.AreEqual(AgentIdentityStatus.Conflict, await service.BeginRotationAsync(oldCredential, RandomNumberGenerator.GetBytes(32), token));
        Assert.IsNull(await service.AuthenticateAsync(newCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Invalid, await service.FinishRotationAsync(oldCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.FinishRotationAsync(newCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.FinishRotationAsync(newCredential, token));
        Assert.IsNull(await service.AuthenticateAsync(oldCredential, token));
        Assert.IsNotNull(await service.AuthenticateAsync(newCredential, token));
        var pending = RandomNumberGenerator.GetBytes(32);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(newCredential, SHA256.HashData(pending), token));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, await service.FinishRotationAsync(Credential(enrolled.AgentId.Value, pending), token));
        Assert.IsNotNull(await service.AuthenticateAsync(newCredential, token));
    });

    [TestMethod]
    public Task ExpiredRevokedAndIneligibleRegistrationsAreRejected() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var actor = Guid.CreateVersion7();
        var first = await service.IssueAsync(device.Id, actor, token);
        var second = await service.IssueAsync(device.Id, actor, token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(first.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(second.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        var third = await service.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var enrolled = await service.RedeemAsync(third.Secret, SHA256.HashData(secret), token);
        Assert.AreEqual(AgentIdentityStatus.Success, enrolled.Status);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.RevokeAsync(enrolled.AgentId!.Value, actor, token));
        var credential = $"vla_{enrolled.AgentId:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.IsNull(await service.AuthenticateAsync(credential, token));
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, current.Address, false, true, clock.GetUtcNow()); await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(AgentIdentityStatus.Conflict, (await service.IssueAsync(device.Id, actor, token)).Status);
    });

    private Task WithDatabase(Func<Factory<ResourcesDbContext>, Factory<AgentManagementDbContext>, CancellationToken, Task> test, bool snapshotReads = false) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var resources = new Factory<ResourcesDbContext>(() => new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options));
            var agents = new Factory<AgentManagementDbContext>(() => new AgentManagementDbContext(new DbContextOptionsBuilder<AgentManagementDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options));
            await using (var db = resources.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = agents.CreateDbContext()) await db.Database.MigrateAsync(token);
            if (snapshotReads)
            {
                var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
                var name = server.InitialCatalog; server.InitialCatalog = "master";
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(server.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            await test(resources, agents, token);
        }, TestContext.CancellationToken);

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext { public T CreateDbContext() => create(); }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static string Credential(Guid id, byte[] secret) => $"vla_{id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
}
