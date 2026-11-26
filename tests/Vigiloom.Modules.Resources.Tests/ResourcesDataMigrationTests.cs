using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Publishing.Persistence;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ResourcesDataMigrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Copy_PreservesDevicesAndGroupMemberships() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var sourceOptions = Options(source.ConnectionString);
            var targetOptions = Options(target.ConnectionString);
            await using (var db = new ResourcesDbContext(sourceOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new ResourcesDbContext(targetOptions)) await db.Database.MigrateAsync(token);
            var telemetryOptions = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(telemetryOptions)) await db.Database.MigrateAsync(token);
            var oldLinksOptions = new DbContextOptionsBuilder<DeploymentLinksDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable(DeploymentLinksDbContext.MigrationHistory, "resources")).Options;
            var newLinksOptions = new DbContextOptionsBuilder<ResourceDeploymentLinksDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(ResourceDeploymentLinksDbContext.MigrationHistory, "resources")).Options;
            await using (var db = new DeploymentLinksDbContext(oldLinksOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new ResourceDeploymentLinksDbContext(newLinksOptions)) await db.Database.MigrateAsync(token);
            var agentOptions = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
            await using (var db = new AgentManagementDbContext(agentOptions)) await db.Database.MigrateAsync(token);
            var package = new Vigiloom.Modules.AgentManagement.Domain.AgentPackage
            {
                Id = Guid.CreateVersion7(), Version = "1.2.3", RuntimeIdentifier = "win-x64",
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Size = 1024
            };
            await using (var db = new AgentManagementDbContext(agentOptions))
            {
                db.Packages.Add(package);
                await db.SaveChangesAsync(token);
            }
            var now = DateTimeOffset.UtcNow;
            var publishingOptions = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "publishing")).Options;
            var template = new Vigiloom.Modules.Publishing.Domain.PublicationTemplate("Default", true, true, now);
            template.SetDefault(true, now);
            await using (var db = new PublishingDbContext(publishingOptions))
            {
                await db.Database.MigrateAsync(token);
                db.Templates.Add(template);
                await db.SaveChangesAsync(token);
            }
            var telemetrySource = new ServiceSource("Source", new byte[32], now);
            var candidate = new ServiceCandidate(telemetrySource.Id, "new-api", "Production", "New API", now);
            var approved = new MonitoredService(new ServiceCandidate(telemetrySource.Id, "order-api", "Production", "Orders", now), now);
            await using (var db = new ServiceObservabilityDbContext(telemetryOptions))
            {
                db.Sources.Add(telemetrySource);
                db.Candidates.Add(candidate);
                db.Services.Add(approved);
                await db.SaveChangesAsync(token);
            }
            var device = new Device("Server", "server.example", true, DateTimeOffset.UtcNow);
            device.SetTemplatePublication("Public server", template.Id, false, now);
            var group = new Group("Servers", DateTimeOffset.UtcNow);
            var serviceId = Guid.CreateVersion7();
            await using (var db = new ResourcesDbContext(sourceOptions))
            {
                db.Devices.Add(device);
                db.Groups.Add(group);
                db.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
                db.ServiceGroups.Add(new ServiceGroup(group.Id, serviceId));
                db.DeviceTargets.Add(new DeviceTarget(device.Id, "extra.example"));
                await db.SaveChangesAsync(token);
            }
            var instanceId = Guid.CreateVersion7();
            await using (var db = new ServiceObservabilityDbContext(telemetryOptions))
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{approved.Id},{telemetrySource.Id},{"node"},{now},{now})", token);
            var link = new DeploymentLink(device.Id, instanceId, Guid.CreateVersion7(), "Verified", now);
            var ended = new DeploymentLink(device.Id, instanceId, Guid.CreateVersion7(), "Previous", now.AddMinutes(-1));
            ended.End(now.AddSeconds(-30));
            await using (var db = new DeploymentLinksDbContext(oldLinksOptions))
            { db.Links.AddRange(link, ended); await db.SaveChangesAsync(token); }
            var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, 0, new byte[32], now);
            var enrollment = new Vigiloom.Modules.AgentManagement.Domain.Enrollment(device.Id, 0, new byte[32], now, now.AddMinutes(15));
            enrollment.Consume(agent.Id);
            var updateId = Guid.CreateVersion7();
            await using (var db = new AgentManagementDbContext(agentOptions))
            {
                db.Agents.Add(agent);
                db.Enrollments.Add(enrollment);
                db.VersionAssignments.Add(new Vigiloom.Modules.AgentManagement.Domain.AgentVersionAssignment
                    { AgentId = agent.Id, PackageId = package.Id, UpdateId = updateId, AssignedBy = Guid.CreateVersion7(), AssignedAt = now });
                db.UpdateTargets.Add(new Vigiloom.Modules.AgentManagement.Domain.AgentUpdateTarget
                    { Id = updateId, AgentId = agent.Id, PackageId = package.Id, Version = package.Version,
                        RuntimeIdentifier = package.RuntimeIdentifier, ManifestSha256 = package.ManifestSha256, AssignedAt = now });
                await db.SaveChangesAsync(token);
            }
            await ResourcesDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token);
            await using (var db = new ResourceDeploymentLinksDbContext(newLinksOptions))
            {
                Assert.IsTrue(await db.Links.AnyAsync(x => x.Id == link.Id && x.DeviceId == device.Id && x.ServiceInstanceId == instanceId && x.EndedAt == null, token));
                Assert.IsTrue(await db.Links.AnyAsync(x => x.Id == ended.Id && x.EndedAt != null, token));
            }
            await using (var db = new ResourcesDbContext(targetOptions))
            {
                Assert.IsTrue(await db.Devices.AnyAsync(x => x.Id == device.Id && x.Address == "server.example", token));
                Assert.IsTrue(await db.DeviceTargets.AnyAsync(x => x.DeviceId == device.Id && x.Address == "extra.example", token));
                Assert.IsTrue(await db.PublicTemplates.AnyAsync(x => x.Id == template.Id && x.IsDefault && x.ShowLatency, token));
                Assert.IsTrue(await db.Devices.AnyAsync(x => x.Id == device.Id && x.PublicTemplateId == template.Id, token));
                Assert.IsTrue(await db.DeviceGroups.AnyAsync(x => x.DeviceId == device.Id && x.GroupId == group.Id, token));
                Assert.IsTrue(await db.ServiceGroups.AnyAsync(x => x.ServiceId == serviceId && x.GroupId == group.Id, token));
                Assert.IsTrue(await db.ApplicationCandidates.AnyAsync(x => x.Id == candidate.Id && x.SourceId == telemetrySource.Id, token));
                Assert.IsTrue(await db.ApplicationServices.AnyAsync(x => x.Id == approved.Id && x.DisplayName == "Orders", token));
                Assert.IsTrue(await db.AgentPackages.AnyAsync(x => x.Id == package.Id && x.Version == package.Version && x.Sha256 == package.Sha256, token));
                Assert.IsTrue(await db.Agents.AnyAsync(x => x.Id == agent.Id && x.DeviceId == device.Id, token));
                Assert.IsTrue(await db.AgentEnrollments.AnyAsync(x => x.Id == enrollment.Id && x.AgentId == agent.Id, token));
                Assert.IsTrue(await db.AgentVersionAssignments.AnyAsync(x => x.AgentId == agent.Id && x.PackageId == package.Id && x.UpdateId == updateId, token));
                Assert.IsTrue(await db.AgentUpdateTargets.AnyAsync(x => x.Id == updateId && x.AgentId == agent.Id && x.PackageId == package.Id, token));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => ResourcesDataMigration.CopyAsync(source.ConnectionString, target.ConnectionString, token));
        }, outerToken), TestContext.CancellationToken);

    private static DbContextOptions<ResourcesDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
}
