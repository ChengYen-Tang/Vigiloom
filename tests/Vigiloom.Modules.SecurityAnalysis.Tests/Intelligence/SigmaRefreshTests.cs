using System.Formats.Tar;
using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Events;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class SigmaRefreshTests(TestContext context)
{
    private const string First = "1111111111111111111111111111111111111111";
    private const string Second = "2222222222222222222222222222222222222222";

    [TestMethod]
    public Task Refresh_StagesCompleteVersionAndKeepsItWhenNextArchiveIsInvalid() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var commit = First;
            var invalid = false;
            var wrongLicense = false;
            var archiveRequests = 0;
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{commit}\"}}") };
                archiveRequests++;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(commit, invalid, wrongLicense)) };
            });
            var refresh = new SigmaRefresh(contexts, clients, clock);
            Assert.AreEqual(SigmaRefreshResult.Staged, await refresh.RunDueAsync(token));
            await using (var check = contexts.CreateDbContext())
            {
                var source = await check.IntelligenceSources.SingleAsync(token);
                var revision = await check.IntelligenceRevisions.SingleAsync(token);
                Assert.AreEqual("sigmahq-rules", source.Key);
                Assert.AreEqual(First, revision.UpstreamVersion);
                Assert.AreEqual(2, revision.RecordCount);
                Assert.AreEqual(2, await check.SigmaRules.CountAsync(token));
                var manifest = await check.SigmaManifests.SingleAsync(token);
                Assert.AreEqual(1, manifest.IncludedCount);
                Assert.AreEqual(1, manifest.ExcludedCount);
                Assert.AreEqual(SigmaManifestBuilder.PolicyVersion, manifest.PolicyVersion);
                Assert.AreEqual("outside-core-rules", (await check.SigmaRules.SingleAsync(x => x.RuleId == null, token)).ExclusionReason);
                Assert.IsNull((await check.SigmaRules.SingleAsync(x => x.RuleId == null, token)).License);
                Assert.AreEqual(0, await check.IntelligenceActivations.CountAsync(token));
                var rules = await new IntelligenceStatus(contexts, clock).GetSigmaRulesAsync(source.Id, revision.Id, 1, token);
                Assert.IsNotNull(rules);
                Assert.HasCount(2, rules.Items);
                Assert.IsTrue(rules.Items.Any(x => x.Path.StartsWith("rules-threat-hunting/", StringComparison.Ordinal)));
                Assert.AreEqual("windows", rules.Items.Single(x => x.RuleId == "abc").Product);
                Assert.IsNull(await new IntelligenceStatus(contexts, clock).GetSigmaRulesAsync(Guid.CreateVersion7(), revision.Id, 1, token));
                Assert.AreEqual(1, await SigmaRevisionEligibility.IncludedCountAsync(check, source.Id, revision.Id, token));
                var eligibleRule = await check.SigmaRules.SingleAsync(x => x.RuleId == "abc", token);
                eligibleRule.Author = "Changed attribution";
                await check.SaveChangesAsync(token);
                Assert.IsNull(await SigmaRevisionEligibility.IncludedCountAsync(check, source.Id, revision.Id, token));
                eligibleRule.Author = "Author";
                await check.SaveChangesAsync(token);
                Assert.AreEqual(1, await SigmaRevisionEligibility.IncludedCountAsync(check, source.Id, revision.Id, token));
                var publisher = new IntelligencePublication(contexts, new OperationAuditWriter(), clock);
                Assert.AreEqual(IntelligencePublicationResult.Published,
                    await publisher.ActivateScheduledAsync(source.Id, revision.Id, null, "", token));
                var catalog = new SigmaCatalog(contexts, clock);
                var selection = await catalog.GetActiveAsync(token);
                Assert.AreEqual(SigmaAvailability.Ready, selection.Availability);
                Assert.AreEqual(1, selection.IncludedCount);
                Assert.AreEqual(1, selection.ExcludedCount);
                var page = await catalog.ReadPageAsync(selection, null, token);
                Assert.AreEqual(SigmaAvailability.Ready, page.Availability);
                Assert.HasCount(1, page.Rules);
                Assert.AreEqual("rules/example.yml", page.Rules[0].Path);
                Assert.AreEqual("Author", page.Rules[0].Author);
                Assert.AreEqual($"https://github.com/SigmaHQ/sigma/blob/{First}/rules/example.yml", page.Rules[0].SourceUrl);
                Assert.AreEqual(SigmaAvailability.Ready, (await catalog.ReadRuleAsync(selection, page.Rules[0].Path, token)).Availability);
                var contentPage = await catalog.ReadContentPageAsync(selection, null, token);
                Assert.AreEqual(SigmaAvailability.Ready, contentPage.Availability);
                Assert.HasCount(1, contentPage.Rules);
                Assert.AreEqual(page.Rules[0].Path, contentPage.Rules[0].Rule!.Path);
                Assert.IsNotNull(contentPage.Rules[0].Content);
                Assert.AreEqual(SigmaAvailability.Unavailable, (await catalog.ReadRuleAsync(selection, "rules-threat-hunting/other.yml", token)).Availability);
                Assert.AreEqual(SigmaAvailability.SelectionChanged,
                    (await catalog.ReadPageAsync(selection with { ActivationVersion = "invalid" }, null, token)).Availability);
                var original = eligibleRule.Content;
                eligibleRule.Content = [1];
                await check.SaveChangesAsync(token);
                Assert.AreEqual(SigmaAvailability.Unavailable, (await catalog.ReadRuleAsync(selection, page.Rules[0].Path, token)).Availability);
                Assert.AreEqual(SigmaAvailability.Unavailable, (await catalog.ReadContentPageAsync(selection, null, token)).Availability);
                eligibleRule.Content = original;
                await check.SaveChangesAsync(token);
                var advancing = new SigmaCatalog(contexts, new AdvancingClock(clock.GetUtcNow()));
                Assert.AreEqual(SigmaAvailability.Stale, (await advancing.GetActiveAsync(token)).Availability);
                advancing = new SigmaCatalog(contexts, new AdvancingClock(clock.GetUtcNow()));
                Assert.AreEqual(SigmaAvailability.Stale, (await advancing.ReadPageAsync(selection, null, token)).Availability);
                advancing = new SigmaCatalog(contexts, new AdvancingClock(clock.GetUtcNow()));
                var expiredContent = await advancing.ReadRuleAsync(selection, page.Rules[0].Path, token);
                Assert.AreEqual(SigmaAvailability.Stale, expiredContent.Availability);
                Assert.IsNull(expiredContent.Content);
                revision.Revoked = true;
                await check.SaveChangesAsync(token);
                Assert.AreEqual(SigmaAvailability.Unavailable, (await catalog.ReadPageAsync(selection, null, token)).Availability);
                revision.Revoked = false;
                await check.SaveChangesAsync(token);
            }
            clock.Advance(TimeSpan.FromDays(2));
            Assert.AreEqual(SigmaRefreshResult.Unchanged, await refresh.RunDueAsync(token));
            Assert.AreEqual(1, archiveRequests);
            clock.Advance(TimeSpan.FromDays(2));
            commit = Second;
            invalid = true;
            Assert.AreEqual(SigmaRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using (var final = contexts.CreateDbContext())
            {
                Assert.AreEqual(1, await final.IntelligenceRevisions.CountAsync(token));
                Assert.AreEqual(2, await final.SigmaRules.CountAsync(token));
                Assert.AreEqual("Sigma source failed validation.", (await final.IntelligenceSources.SingleAsync(token)).LastError);
            }
            clock.Advance(TimeSpan.FromHours(1));
            invalid = false;
            wrongLicense = true;
            Assert.AreEqual(SigmaRefreshResult.Failed, await refresh.RunDueAsync(token));
            await using var licenseCheck = contexts.CreateDbContext();
            Assert.AreEqual(1, await licenseCheck.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual("Sigma license unavailable or changed.", (await licenseCheck.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public Task SourceSelection_CanSwitchRollbackAndRejectRevokedVersion() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var commit = First;
            string Rule(string id) => $"title: Example\nid: abc\nauthor: Author\nlogsource: {{product: windows, service: security}}\ndetection:\n  selection: {{EventID: {id}}}\n  condition: selection\n";
            var clients = new ClientFactory(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{commit}\"}}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(commit, false, false,
                    ruleContent: Rule(commit == First ? "4625" : "4624"), additionalRuleContent: Rule("4625").Replace("id: abc", "id: def"))) });
            var refresh = new SigmaRefresh(contexts, clients, clock);
            Assert.AreEqual(SigmaRefreshResult.Staged, await refresh.RunDueAsync(token));
            Guid sourceId;
            Guid firstId;
            await using (var db = contexts.CreateDbContext())
            {
                sourceId = (await db.IntelligenceSources.SingleAsync(token)).Id;
                firstId = (await db.IntelligenceRevisions.SingleAsync(token)).Id;
            }
            var actor = Guid.CreateVersion7();
            var publisher = new IntelligencePublication(contexts, new OperationAuditWriter(), clock);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, firstId, null, "", false, actor, token));
            clock.Advance(TimeSpan.FromDays(2));
            commit = Second;
            Assert.AreEqual(SigmaRefreshResult.Staged, await refresh.RunDueAsync(token));
            Guid secondId;
            string version;
            await using (var db = contexts.CreateDbContext())
            {
                secondId = (await db.IntelligenceRevisions.SingleAsync(x => x.UpstreamVersion == Second, token)).Id;
                version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            }
            var catalog = new SigmaCatalog(contexts, clock);
            var firstSelection = await catalog.GetActiveAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, secondId, firstId, version, false, actor, token));
            Assert.AreEqual(SigmaAvailability.SelectionChanged, (await catalog.ReadPageAsync(firstSelection, null, token)).Availability);
            var agent = Guid.CreateVersion7();
            HostLogEntry Event(string id) => new(agent, Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
                "test", 1, clock.GetUtcNow(), clock.GetUtcNow(),
                $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>{id}</EventID><Channel>Security</Channel></System></Event>",
                null, null, null, null, null, null);
            var oldMatch = Event("4625");
            var newMatch = Event("4624");
            var reader = new ReplayLogReader([oldMatch, newMatch]);
            var testing = new SigmaTesting(contexts, catalog, reader, clock);
            var management = new SigmaRuleManagement(contexts, catalog, new OperationAuditWriter(), clock);
            var newerTest = await testing.RunAsync("rules/example.yml",
                [new(agent, newMatch.MessageId, SigmaMatch.Yes), new(agent, oldMatch.MessageId, SigmaMatch.No)], actor, token);
            Assert.IsTrue(newerTest.Passed.GetValueOrDefault());
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(sourceId, "rules/example.yml", newerTest.TestId, true, "", actor, token));
            await using (var db = contexts.CreateDbContext()) version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Published, await publisher.ActivateAsync(sourceId, firstId, secondId, version, false, actor, token));
            var enabledRules = new SigmaEnabledRules(contexts, new SigmaRuleSetLoader(catalog));
            Assert.IsEmpty((await enabledRules.LoadAsync(token)).Rules.Rules);
            var olderTest = await testing.RunAsync("rules/example.yml",
                [new(agent, oldMatch.MessageId, SigmaMatch.Yes), new(agent, newMatch.MessageId, SigmaMatch.No)], actor, token);
            Assert.IsTrue(olderTest.Passed.GetValueOrDefault());
            await using (var db = contexts.CreateDbContext()) version = Convert.ToBase64String((await db.SigmaRuleActivations.SingleAsync(token)).Version);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(sourceId, "rules/example.yml", olderTest.TestId, true, version, actor, token));
            var device = Guid.CreateVersion7();
            var dispatch = new HostLogDispatch(agent, device, 1, [oldMatch.MessageId, newMatch.MessageId]);
            var timeline = new DetectionTimeline();
            var handler = new HostLogDispatchHandler(contexts, enabledRules, catalog, reader, clock, new NotificationWriter(), timeline);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType, JsonSerializer.Serialize(dispatch)),
                    connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            await using (var db = contexts.CreateDbContext())
            {
                var result = await db.SigmaDetectionResults.AsNoTracking().SingleAsync(token);
                Assert.AreEqual(oldMatch.MessageId, result.MessageId);
                Assert.AreEqual(olderTest.TestId, result.TestId);
            }
            var timelineEntry = Assert.ContainsSingle(timeline.Items);
            Assert.AreEqual("sigma", timelineEntry.Source);
            Assert.AreEqual(device, timelineEntry.ResourceId);
            Assert.AreEqual($"/events/rules/{olderTest.TestId:D}", timelineEntry.RuleLink);
            await using (var db = new OperationsDbContext(operations))
            {
                var notification = await db.Set<Notification>().SingleAsync(token);
                Assert.AreEqual("sigma", notification.Source);
                Assert.AreEqual(device, notification.ResourceId);
                Assert.AreEqual(agent, notification.EvidenceOwnerId);
                Assert.AreEqual(oldMatch.MessageId, notification.EvidenceId);
                Assert.AreEqual(olderTest.TestId, notification.RuleTestId);
                Assert.Contains(oldMatch.MessageId.ToString("D"), notification.Detail);
                Assert.Contains(olderTest.TestId!.Value.ToString("D"), notification.Detail);
            }
            var secondRuleTest = await testing.RunAsync("rules/second.yml",
                [new(agent, oldMatch.MessageId, SigmaMatch.Yes), new(agent, newMatch.MessageId, SigmaMatch.No)], actor, token);
            Assert.IsTrue(secondRuleTest.Passed.GetValueOrDefault());
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(sourceId, "rules/second.yml", secondRuleTest.TestId, true, "", actor, token));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType, JsonSerializer.Serialize(dispatch)),
                    connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            await using (var db = contexts.CreateDbContext())
            {
                var results = await db.SigmaDetectionResults.AsNoTracking().ToArrayAsync(token);
                Assert.HasCount(2, results);
                Assert.IsTrue(results.All(x => x.MessageId == oldMatch.MessageId));
                CollectionAssert.AreEquivalent(new[] { olderTest.TestId!.Value, secondRuleTest.TestId!.Value }, results.Select(x => x.TestId).ToArray());
            }
            await using (var db = new OperationsDbContext(operations))
                Assert.AreEqual(2, await db.Set<Notification>().CountAsync(token));
            Assert.AreEqual(IntelligenceRevocationResult.Revoked, await new IntelligenceRevocation(contexts, new OperationAuditWriter(), clock)
                .RevokeAsync(sourceId, secondId, actor, token));
            await using (var db = contexts.CreateDbContext()) version = Convert.ToBase64String((await db.IntelligenceActivations.SingleAsync(token)).Version);
            Assert.AreEqual(IntelligencePublicationResult.Unavailable, await publisher.ActivateAsync(sourceId, secondId, firstId, version, false, actor, token));
            Assert.AreEqual(SigmaAvailability.Ready, (await catalog.GetActiveAsync(token)).Availability);
        }, context.CancellationToken);

    [TestMethod]
    public Task CorrelationSource_PreservesAttributionAndDoesNotEnableUnsupportedExecution() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            const string rule = "title: Many failures\nid: abc\nauthor: Author\ncorrelation:\n  type: event_count\n  rules: [failed_login]\n  group-by: [TargetUserName]\n  timespan: 5m\n  condition: {gte: 10}\n";
            var clients = new ClientFactory(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, false, false, ruleContent: rule)) });
            Assert.AreEqual(SigmaRefreshResult.Staged, await new SigmaRefresh(contexts, clients, clock).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var source = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await new IntelligencePublication(contexts, new OperationAuditWriter(), clock).ActivateScheduledAsync(source.Id, revision.Id, null, "", token));
            var catalog = new SigmaCatalog(contexts, clock);
            var snapshot = await catalog.GetActiveAsync(token);
            var retained = await catalog.ReadRuleAsync(snapshot, "rules/example.yml", token);
            Assert.AreEqual(SigmaAvailability.Ready, retained.Availability);
            Assert.IsNotNull(retained.Content);
            Assert.IsNotNull(retained.Rule);
            Assert.AreEqual("Author", retained.Rule.Author);
            Assert.IsNotNull(CorrelationCompiler.Compile(retained.Content, TimeSpan.FromDays(7)).Rule);
            Assert.IsNull(SigmaRuleCompiler.Compile(retained.Content).Rule);
        }, context.CancellationToken);

    [TestMethod]
    public Task Catalog_UsesBoundedPagesAndRejectsSelectionChanges() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            var clients = new ClientFactory(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, false, false, extraRules: 55)) });
            Assert.AreEqual(SigmaRefreshResult.Staged, await new SigmaRefresh(contexts, clients, clock).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var source = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await new IntelligencePublication(contexts, new OperationAuditWriter(), clock).ActivateScheduledAsync(source.Id, revision.Id, null, "", token));
            var catalog = new SigmaCatalog(contexts, clock);
            var selection = await catalog.GetActiveAsync(token);
            var first = await catalog.ReadPageAsync(selection, null, token);
            Assert.HasCount(50, first.Rules);
            Assert.IsNotNull(first.NextPath);
            var second = await catalog.ReadPageAsync(selection, first.NextPath, token);
            Assert.HasCount(6, second.Rules);
            Assert.IsNull(second.NextPath);
            Assert.AreEqual(56, first.Rules.Concat(second.Rules).Select(x => x.Path).Distinct().Count());
            var contentFirst = await catalog.ReadContentPageAsync(selection, null, token);
            Assert.HasCount(50, contentFirst.Rules);
            Assert.IsNotNull(contentFirst.NextPath);
            var contentSecond = await catalog.ReadContentPageAsync(selection, contentFirst.NextPath, token);
            Assert.HasCount(6, contentSecond.Rules);
            Assert.IsNull(contentSecond.NextPath);
            Assert.AreEqual(56, contentFirst.Rules.Concat(contentSecond.Rules).Select(x => x.Rule!.Path).Distinct().Count());
            var inventory = new SigmaRuleInventory(contexts, catalog);
            var inventoryFirst = await inventory.GetAsync(null, token);
            Assert.HasCount(50, inventoryFirst.Items);
            Assert.IsNotNull(inventoryFirst.NextCursor);
            var active = await db.IntelligenceActivations.SingleAsync(token);
            active.ActivatedAt = active.ActivatedAt.AddSeconds(1);
            await db.SaveChangesAsync(token);
            var changed = await inventory.GetAsync(inventoryFirst.NextCursor, token);
            Assert.AreEqual(SigmaAvailability.SelectionChanged, changed.Source.Availability);
            Assert.IsEmpty(changed.Items);
            source.Enabled = false;
            await db.SaveChangesAsync(token);
            Assert.AreEqual(SigmaAvailability.Disabled, (await catalog.ReadPageAsync(selection, first.NextPath, token)).Availability);
            Assert.AreEqual(SigmaAvailability.Disabled, (await catalog.ReadContentPageAsync(selection, contentFirst.NextPath, token)).Availability);
        }, context.CancellationToken);

    [TestMethod]
    public Task CorrelationRuleTesting_SavesBaseRuleAndPolicyEvidence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var init = contexts.CreateDbContext()) await init.Database.MigrateAsync(token);
            await using (var init = new OperationsDbContext(operations)) await init.Database.MigrateAsync(token);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            const string rule = "title: Failed login\nname: failed_login\nauthor: Author\nlogsource: {product: windows, service: security}\ndetection:\n  selection: {EventID: 4625}\n  condition: selection\n";
            const string correlation = "title: Repeated login failure\nauthor: Author\ncorrelation:\n  type: event_count\n  rules: [failed_login]\n  group-by: [TargetUserName]\n  timespan: 5m\n  condition: {gte: 3}\n";
            var clients = new ClientFactory(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, false, false, ruleContent: rule, correlationContent: correlation)) });
            Assert.AreEqual(SigmaRefreshResult.Staged, await new SigmaRefresh(contexts, clients, clock).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var source = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await new IntelligencePublication(contexts, new OperationAuditWriter(), clock).ActivateScheduledAsync(source.Id, revision.Id, null, "", token));
            var agent = Guid.CreateVersion7();
            HostLogEntry Event(int seconds, bool user) => new(agent, Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
                "test", 1, clock.GetUtcNow().AddSeconds(seconds), clock.GetUtcNow().AddSeconds(seconds),
                $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>4625</EventID><Channel>Security</Channel></System><EventData>{(user ? "<Data Name=\"TargetUserName\">alice</Data>" : "")}</EventData></Event>",
                null, null, null, null, null, null);
            var first = Event(0, true);
            var second = Event(1, true);
            var third = Event(2, true);
            var missing = Event(600, false);
            var testing = new SigmaTesting(contexts, new SigmaCatalog(contexts, clock), new ReplayLogReader([first, second, third, missing]), clock);
            var samples = new[] { new SigmaTestSample(agent, first.MessageId, SigmaMatch.No), new SigmaTestSample(agent, second.MessageId, SigmaMatch.No),
                new SigmaTestSample(agent, third.MessageId, SigmaMatch.Yes), new SigmaTestSample(agent, missing.MessageId, SigmaMatch.Unknown) };
            var result = await testing.RunAsync("rules/correlation.yml", samples, Guid.CreateVersion7(), token);
            Assert.AreEqual(SigmaTestingStatus.Saved, result.Status);
            Assert.IsTrue(result.Passed.GetValueOrDefault());
            var saved = await db.SigmaRuleTests.AsNoTracking().SingleAsync(token);
            using var evidence = JsonDocument.Parse(saved.SamplesJson);
            Assert.AreEqual("rules/example.yml", evidence.RootElement.GetProperty("BasePath").GetString());
            Assert.AreEqual(64, evidence.RootElement.GetProperty("BaseSha256").GetString()!.Length);
            Assert.AreEqual(300, evidence.RootElement.GetProperty("MaximumLatenessSeconds").GetInt32());
            Assert.AreEqual(4, evidence.RootElement.GetProperty("Samples").GetArrayLength());
            SigmaCorrelationEvent State(byte version) => new()
            {
                TestId = saved.Id, ActivationVersion = [0, 0, 0, 0, 0, 0, 0, version], AgentId = agent,
                MessageId = first.MessageId, DeviceId = Guid.CreateVersion7(), DeviceGeneration = 1,
                OccurredAt = first.OccurredAt, ReceivedAt = first.ReceivedAt
            };
            db.SigmaCorrelationEvents.AddRange(State(1), State(2));
            await db.SaveChangesAsync(token);
            Assert.AreEqual(2, await db.SigmaCorrelationEvents.AsNoTracking().CountAsync(token));
            var duplicate = State(1);
            db.SigmaCorrelationEvents.Add(duplicate);
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.Entry(duplicate).State = EntityState.Detached;
            var correlationVersion = new SigmaEnabledRuleVersion(Guid.CreateVersion7(), saved.Id, "rules/correlation.yml", [0, 0, 0, 0, 0, 0, 0, 3], []);
            var store = new CorrelationEventStore(new ReplayLogReader([first, second, third, missing]));
            var plan = Assert.ContainsSingle((await new SigmaRuleSetLoader(new SigmaCatalog(contexts, clock)).LoadAsync(token)).Correlations).Plan;
            var device = Guid.CreateVersion7();
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var firstBatch = await store.CaptureAsync(db, correlationVersion, new(agent, device, 1, [first.MessageId, second.MessageId]),
                    [first, second], clock.GetUtcNow(), plan.Rule.Window, token);
                Assert.IsFalse(firstBatch.CapacityExceeded);
                Assert.HasCount(2, firstBatch.References);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var missingId = Guid.CreateVersion7();
                var next = await store.CaptureAsync(db, correlationVersion, new(agent, device, 1, [second.MessageId, third.MessageId, missingId]),
                    [second, third], clock.GetUtcNow(), plan.Rule.Window, token);
                Assert.IsFalse(next.CapacityExceeded);
                Assert.HasCount(4, next.References);
                var evaluated = CorrelationEvaluation.Evaluate(plan, next.Events, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20, token);
                Assert.AreEqual(SigmaMatch.Yes, evaluated.Observations.Single(x => x.Event.MessageId == third.MessageId).Result);
                Assert.AreEqual(SigmaMatch.Unknown, evaluated.Observations.Single(x => x.Event.MessageId == missingId).Result);
                await db.SaveChangesAsync(token);
                await transaction.RollbackAsync(token);
            }
            db.ChangeTracker.Clear();
            Assert.AreEqual(4, await db.SigmaCorrelationEvents.AsNoTracking().CountAsync(token));
            var later = Event(1200, true);
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var pruned = await store.CaptureAsync(db, correlationVersion, new(agent, device, 1, [later.MessageId]),
                    [later], clock.GetUtcNow().AddMinutes(20), plan.Rule.Window, token);
                Assert.HasCount(1, pruned.References);
                Assert.IsGreaterThan(clock.GetUtcNow(), pruned.CompleteSince);
                await db.SaveChangesAsync(token);
                await transaction.RollbackAsync(token);
            }
            db.ChangeTracker.Clear();
            Assert.AreEqual(4, await db.SigmaCorrelationEvents.AsNoTracking().CountAsync(token));
            var limitedVersion = correlationVersion with { Version = [0, 0, 0, 0, 0, 0, 0, 4] };
            var limitedStore = new CorrelationEventStore(new ReplayLogReader([first, second]), maximumEvents: 1);
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var overflow = await limitedStore.CaptureAsync(db, limitedVersion, new(agent, device, 1, [first.MessageId, second.MessageId]),
                    [first, second], clock.GetUtcNow(), plan.Rule.Window, token);
                Assert.IsTrue(overflow.CapacityExceeded);
                Assert.IsNotNull(overflow.IncompleteUntil);
                Assert.AreEqual(second.OccurredAt!.Value.Add(plan.Rule.Window), overflow.IncompleteUntil.Value);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var recovered = await limitedStore.CaptureAsync(db, limitedVersion, new(agent, device, 1, [first.MessageId]),
                    [first], clock.GetUtcNow().AddMinutes(10), plan.Rule.Window, token);
                Assert.IsFalse(recovered.CapacityExceeded);
                Assert.IsNotNull(recovered.IncompleteUntil);
                Assert.IsGreaterThan(recovered.IncompleteUntil.Value, clock.GetUtcNow().AddMinutes(10));
                await transaction.RollbackAsync(token);
            }
            db.ChangeTracker.Clear();
            var skewed = first with { MessageId = Guid.CreateVersion7(), OccurredAt = clock.GetUtcNow().AddDays(2) };
            var skewVersion = correlationVersion with { Version = [0, 0, 0, 0, 0, 0, 0, 5] };
            var skewStore = new CorrelationEventStore(new ReplayLogReader([skewed, later]), maximumEvents: 1);
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var initial = await skewStore.CaptureAsync(db, skewVersion, new(agent, device, 1, [skewed.MessageId]),
                    [skewed], clock.GetUtcNow(), plan.Rule.Window, token);
                Assert.IsFalse(initial.CapacityExceeded);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var recovered = await skewStore.CaptureAsync(db, skewVersion, new(agent, device, 1, [later.MessageId]),
                    [later], clock.GetUtcNow().AddMinutes(20), plan.Rule.Window, token);
                Assert.IsFalse(recovered.CapacityExceeded);
                Assert.AreEqual(later.MessageId, Assert.ContainsSingle(recovered.References).MessageId);
                await transaction.RollbackAsync(token);
            }
            db.ChangeTracker.Clear();
            var management = new SigmaRuleManagement(contexts, new SigmaCatalog(contexts, clock), new OperationAuditWriter(), clock);
            var actor = Guid.CreateVersion7();
            var inventory = new SigmaRuleInventory(contexts, new SigmaCatalog(contexts, clock));
            var beforeEnable = (await inventory.GetAsync(null, token)).Items.Single(x => x.Source.Path == "rules/correlation.yml");
            Assert.IsTrue(beforeEnable.Supported);
            Assert.IsFalse(beforeEnable.Enabled);
            await db.SigmaRuleTests.Where(x => x.Id == saved.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.SamplesJson, "{}"), token);
            Assert.AreEqual(SigmaRuleChangeResult.TestUnavailable,
                await management.SetAsync(source.Id, "rules/correlation.yml", result.TestId, true, "", actor, token));
            await db.SigmaRuleTests.Where(x => x.Id == saved.Id).ExecuteUpdateAsync(set => set.SetProperty(x => x.SamplesJson, saved.SamplesJson), token);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/correlation.yml", result.TestId, true, "", actor, token));
            var activation = await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token);
            var afterEnable = (await inventory.GetAsync(null, token)).Items.Single(x => x.Source.Path == "rules/correlation.yml");
            Assert.IsTrue(afterEnable.EnabledForCurrentSource);
            SigmaDetectionResult Detection(byte version) => new()
            {
                AgentId = agent, MessageId = first.MessageId, DeviceId = Guid.CreateVersion7(), DeviceGeneration = 1,
                ActivationId = activation.Id, TestId = saved.Id, ActivationVersion = [0, 0, 0, 0, 0, 0, 0, version],
                Result = (int)SigmaMatch.Yes, OccurredAt = first.OccurredAt, ReceivedAt = first.ReceivedAt, EvaluatedAt = clock.GetUtcNow()
            };
            db.SigmaDetectionResults.AddRange(Detection(1), Detection(2));
            await db.SaveChangesAsync(token);
            Assert.AreEqual(2, await db.SigmaDetectionResults.AsNoTracking().CountAsync(token));
            var duplicateResult = Detection(1);
            db.SigmaDetectionResults.Add(duplicateResult);
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            db.Entry(duplicateResult).State = EntityState.Detached;
            var activeVersion = activation.Version.ToArray();
            var enabled = new SigmaEnabledRules(contexts, new SigmaRuleSetLoader(new SigmaCatalog(contexts, clock)));
            Assert.HasCount(1, (await enabled.LoadAsync(token)).Rules.Correlations);
            var handler = new HostLogDispatchHandler(contexts, enabled, new SigmaCatalog(contexts, clock),
                new ReplayLogReader([first, second, third, missing]), clock, new NotificationWriter());
            var earlierAnchor = new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType,
                JsonSerializer.Serialize(new HostLogDispatch(agent, device, 1, [third.MessageId])));
            var delayedBatch = new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType,
                JsonSerializer.Serialize(new HostLogDispatch(agent, device, 1, [first.MessageId, second.MessageId])));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
                {
                    await handler.HandleAsync(earlierAnchor, connection, transaction, token);
                    await transaction.CommitAsync(token);
                }
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
                {
                    await handler.HandleAsync(delayedBatch, connection, transaction, token);
                    await transaction.RollbackAsync(token);
                }
                var before = (await db.SigmaDetectionResults.AsNoTracking().Where(x => x.MessageId == third.MessageId).ToArrayAsync(token))
                    .Single(x => x.ActivationVersion.AsSpan().SequenceEqual(activeVersion));
                Assert.AreEqual((int)SigmaMatch.Unknown, before.Result);
                await using (var notificationDb = new OperationsDbContext(operations))
                    Assert.AreEqual(0, await notificationDb.Set<Notification>().CountAsync(token));
                for (var i = 0; i < 2; i++)
                {
                    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                    await handler.HandleAsync(delayedBatch, connection, transaction, token);
                    await transaction.CommitAsync(token);
                }
            }
            var after = (await db.SigmaDetectionResults.AsNoTracking().Where(x => x.MessageId == third.MessageId).ToArrayAsync(token))
                .Single(x => x.ActivationVersion.AsSpan().SequenceEqual(activeVersion));
            Assert.AreEqual((int)SigmaMatch.Yes, after.Result);
            Assert.AreEqual(3, after.CorrelationCount);
            Assert.AreEqual(first.OccurredAt, after.CorrelationFirstAt);
            Assert.AreEqual(third.OccurredAt, after.CorrelationLastAt);
            await using (var notificationDb = new OperationsDbContext(operations))
            {
                var notification = await notificationDb.Set<Notification>().SingleAsync(token);
                Assert.AreEqual("sigma", notification.Source);
                Assert.Contains(third.MessageId.ToString("D"), notification.Detail);
            }
            var history = await new SigmaDetectionHistory(contexts, new DetectionDeviceSource(device)).GetEventsAsync(device,
                clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow().AddMinutes(1), null, token);
            Assert.IsNotNull(history);
            var match = history.Items.Single(x => x.MessageId == third.MessageId && x.ActivationVersion == Convert.ToBase64String(activeVersion));
            Assert.IsNotNull(match.Correlation);
            Assert.AreEqual(3, match.Correlation.Count);
            Assert.AreEqual(SigmaRuleChangeResult.Changed, await management.SetAsync(source.Id, "rules/correlation.yml", null, false,
                Convert.ToBase64String(activeVersion), actor, token));
            var disabledVersion = (await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version;
            Assert.AreEqual(SigmaRuleChangeResult.Changed, await management.SetAsync(source.Id, "rules/correlation.yml", result.TestId, true,
                Convert.ToBase64String(disabledVersion), actor, token));
            var restartedVersion = (await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version;
            Assert.IsFalse(activeVersion.AsSpan().SequenceEqual(restartedVersion));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(earlierAnchor, connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            var restarted = (await db.SigmaDetectionResults.AsNoTracking().Where(x => x.MessageId == third.MessageId).ToArrayAsync(token))
                .Single(x => x.ActivationVersion.AsSpan().SequenceEqual(restartedVersion));
            Assert.AreEqual((int)SigmaMatch.Unknown, restarted.Result);
        }, context.CancellationToken);

    [TestMethod]
    public Task RuleTesting_SavesEvidenceAndRejectsMissingOrChangedSource() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            var contexts = new Factory(options);
            await using (var init = contexts.CreateDbContext()) await init.Database.MigrateAsync(token);
            await using (var init = new OperationsDbContext(operations)) await init.Database.MigrateAsync(token);
            var clock = new TestClock(DateTimeOffset.UtcNow);
            const string rule = "title: Example\nid: abc\nauthor: Author\nlogsource: {product: windows, service: security}\ndetection:\n  selection: {EventID: 4625}\n  condition: selection\n";
            var clients = new ClientFactory(request => request.RequestUri!.Host == "api.github.com"
                ? new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") }
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, false, false, ruleContent: rule)) });
            Assert.AreEqual(SigmaRefreshResult.Staged, await new SigmaRefresh(contexts, clients, clock).RunDueAsync(token));
            await using var db = contexts.CreateDbContext();
            var source = await db.IntelligenceSources.SingleAsync(token);
            var revision = await db.IntelligenceRevisions.SingleAsync(token);
            Assert.AreEqual(IntelligencePublicationResult.Published,
                await new IntelligencePublication(contexts, new OperationAuditWriter(), clock).ActivateScheduledAsync(source.Id, revision.Id, null, "", token));
            var agent = Guid.CreateVersion7();
            HostLogEntry Event(string id) => new(agent, Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.WindowsEvent,
                "test", 1, clock.GetUtcNow(), clock.GetUtcNow(),
                $"<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\"><System><EventID>{id}</EventID><Channel>Security</Channel></System></Event>",
                null, null, null, null, null, null);
            var positive = Event("4625");
            var negative = Event("4624");
            var samples = new[] { new SigmaTestSample(agent, positive.MessageId, SigmaMatch.Yes), new SigmaTestSample(agent, negative.MessageId, SigmaMatch.No) };
            var reader = new ReplayLogReader([positive, negative]);
            var testing = new SigmaTesting(contexts, new SigmaCatalog(contexts, clock), reader, clock);
            var saved = await testing.RunAsync("rules/example.yml", samples, Guid.CreateVersion7(), token);
            Assert.AreEqual(SigmaTestingStatus.Saved, saved.Status);
            Assert.IsTrue(saved.Passed.GetValueOrDefault());
            var evidence = await db.SigmaRuleTests.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(revision.Id, evidence.RevisionId);
            using var storedSamples = JsonDocument.Parse(evidence.SamplesJson);
            foreach (var sample in storedSamples.RootElement.GetProperty("Samples").EnumerateArray())
                Assert.AreEqual("Actual,AgentId,ContentSha256,Expected,MessageId",
                    string.Join(',', sample.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)));
            Assert.Contains(positive.MessageId.ToString(), evidence.SamplesJson);
            var failed = await testing.RunAsync("rules/example.yml",
                [samples[0] with { Expected = SigmaMatch.No }, samples[1] with { Expected = SigmaMatch.Yes }], Guid.CreateVersion7(), token);
            Assert.AreEqual(SigmaTestingStatus.Saved, failed.Status);
            Assert.IsFalse(failed.Passed.GetValueOrDefault(true));
            var management = new SigmaRuleManagement(contexts, new SigmaCatalog(contexts, clock), new OperationAuditWriter(), clock);
            var actor = Guid.CreateVersion7();
            Assert.AreEqual(SigmaRuleChangeResult.TestUnavailable,
                await management.SetAsync(source.Id, "rules/example.yml", failed.TestId, true, "", actor, token));
            var failingAudit = new SigmaRuleManagement(contexts, new SigmaCatalog(contexts, clock), new RejectAudit(), clock);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                failingAudit.SetAsync(source.Id, "rules/example.yml", saved.TestId, true, "", actor, token));
            Assert.AreEqual(0, await db.SigmaRuleActivations.AsNoTracking().CountAsync(token));
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", saved.TestId, true, "", actor, token));
            var enabledRules = new SigmaEnabledRules(contexts, new SigmaRuleSetLoader(new SigmaCatalog(contexts, clock)));
            var loaded = await enabledRules.LoadAsync(token);
            Assert.HasCount(1, loaded.Rules.Rules);
            Assert.HasCount(1, loaded.Versions);
            Assert.AreEqual(saved.TestId, loaded.Versions[0].TestId);
            Assert.IsEmpty(loaded.UnavailablePaths);
            var inventory = new SigmaRuleInventory(contexts, new SigmaCatalog(contexts, clock));
            var listed = await inventory.GetAsync(null, token);
            var listedRule = Assert.ContainsSingle(listed.Items);
            Assert.IsTrue(listedRule.Supported);
            Assert.IsTrue(listedRule.Enabled);
            Assert.IsTrue(listedRule.EnabledForCurrentSource);
            Assert.AreEqual(failed.TestId, listedRule.LatestTestId);
            Assert.IsFalse(listedRule.LatestTestPassed.GetValueOrDefault(true));
            var missingId = Guid.CreateVersion7();
            var dispatch = new HostLogDispatch(agent, Guid.CreateVersion7(), 1, [positive.MessageId, negative.MessageId, missingId]);
            var handler = new HostLogDispatchHandler(contexts, enabledRules, new SigmaCatalog(contexts, clock), reader, clock);
            var message = new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType, JsonSerializer.Serialize(dispatch));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
                {
                    await handler.HandleAsync(message, connection, transaction, token);
                    await transaction.RollbackAsync(token);
                }
                Assert.AreEqual(0, await db.SigmaDetectionResults.CountAsync(token));
                for (var i = 0; i < 2; i++)
                {
                    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                    await handler.HandleAsync(message, connection, transaction, token);
                    await transaction.CommitAsync(token);
                }
            }
            var detection = await db.SigmaDetectionResults.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(positive.MessageId, detection.MessageId);
            Assert.AreEqual(saved.TestId, detection.TestId);
            Assert.AreEqual((int)SigmaMatch.Yes, detection.Result);
            var missing = await db.SigmaEventGaps.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(missingId, missing.MessageId);
            Assert.AreEqual(1, missing.Kind);
            var history = new SigmaDetectionHistory(contexts, new DetectionDeviceSource(dispatch.DeviceId));
            var historyFrom = clock.GetUtcNow().AddMinutes(-1);
            var historyUntil = clock.GetUtcNow().AddMinutes(1);
            var historyEvents = await history.GetEventsAsync(dispatch.DeviceId, historyFrom, historyUntil, null, token);
            Assert.IsNotNull(historyEvents);
            var match = Assert.ContainsSingle(historyEvents.Items);
            Assert.AreEqual(positive.MessageId, match.MessageId);
            Assert.AreEqual(revision.Id, match.RevisionId);
            Assert.AreEqual(saved.TestId, match.TestId);
            Assert.AreEqual(SigmaTesting.MatcherVersion, match.MatcherVersion);
            Assert.AreEqual(Convert.ToBase64String(loaded.Versions[0].Version), match.ActivationVersion);
            Assert.AreEqual(SigmaExpectedOutcome.Match, match.Outcome);
            var historyGaps = await history.GetGapsAsync(dispatch.DeviceId, historyFrom, historyUntil, null, token);
            Assert.IsNotNull(historyGaps);
            Assert.AreEqual(SigmaDetectionGapKind.OriginalEventUnavailable, Assert.ContainsSingle(historyGaps.Items).Kind);
            Assert.IsNull(await history.GetEventsAsync(Guid.CreateVersion7(), historyFrom, historyUntil, null, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => history.GetEventsAsync(dispatch.DeviceId, historyFrom, historyUntil, "bad", token));
            var previousDetection = db.Database.GetMigrations().Single(x => x.EndsWith("_SigmaDetection", StringComparison.Ordinal));
            var downgrade = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(previousDetection, token));
            Assert.AreEqual(51000, downgrade.Number);
            await db.Database.MigrateAsync(token);
            Assert.AreEqual(1, await db.SigmaDetectionResults.CountAsync(token));
            await using (var check = contexts.CreateDbContext())
            await using (var transaction = await check.Database.BeginTransactionAsync(token))
                Assert.IsTrue(await SigmaEnabledRules.StillCurrentAsync(check, new SigmaCatalog(contexts, clock), loaded, token));
            var noise = Event("4625");
            noise = noise with { Content = noise.Content.Replace("</Event>", "<EventData><Data Name=\"Origin\">health-check</Data></EventData></Event>") };
            reader.Events = [positive, negative, noise];
            var adjusted = await testing.RunAsync("rules/example.yml",
                [samples[0], samples[1], new(agent, noise.MessageId, SigmaMatch.No)], actor, token,
                [new("Origin", "health-check")]);
            Assert.IsTrue(adjusted.Passed.GetValueOrDefault());
            var activeVersion = Convert.ToBase64String((await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", adjusted.TestId, true, activeVersion, actor, token));
            var adjustedRules = await enabledRules.LoadAsync(token);
            Assert.AreEqual("health-check", Assert.ContainsSingle(Assert.ContainsSingle(adjustedRules.Versions).Exceptions).Value);
            var noiseDispatch = new OutboxMessage(Guid.CreateVersion7(), HostLogDispatch.MessageType,
                JsonSerializer.Serialize(new HostLogDispatch(agent, dispatch.DeviceId, 1, [noise.MessageId])));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(noiseDispatch, connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            Assert.IsFalse(await db.SigmaDetectionResults.AsNoTracking().AnyAsync(x => x.MessageId == noise.MessageId, token));
            activeVersion = Convert.ToBase64String((await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", saved.TestId, true, activeVersion, actor, token));
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(noiseDispatch, connection, transaction, token);
                await using var query = new SqlCommand("SELECT COUNT(*) FROM [security].[SigmaDetectionResults] WHERE [MessageId] = @messageId", connection, transaction);
                query.Parameters.AddWithValue("@messageId", noise.MessageId);
                Assert.AreEqual(1, (int)(await query.ExecuteScalarAsync(token))!);
                await transaction.RollbackAsync(token);
            }
            reader.Events = [positive, negative];
            await db.SigmaRuleTests.Where(x => x.Id == saved.TestId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Digest, new string('0', 64)), token);
            var changedRule = await enabledRules.LoadAsync(token);
            Assert.IsEmpty(changedRule.Rules.Rules);
            Assert.Contains("rules/example.yml", changedRule.UnavailablePaths);
            var changedListing = Assert.ContainsSingle((await inventory.GetAsync(null, token)).Items);
            Assert.IsTrue(changedListing.Enabled);
            Assert.IsFalse(changedListing.EnabledForCurrentSource);
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(token);
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
                await handler.HandleAsync(message, connection, transaction, token);
                await transaction.CommitAsync(token);
            }
            Assert.AreEqual(2, await db.SigmaEventGaps.CountAsync(x => x.Kind == 2, token));
            Assert.AreEqual(1, await db.SigmaDetectionResults.CountAsync(token));
            await db.SigmaRuleTests.Where(x => x.Id == saved.TestId).ExecuteUpdateAsync(set => set.SetProperty(x => x.Digest, evidence.Digest), token);
            var setting = await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token);
            var version = Convert.ToBase64String(setting.Version);
            Assert.AreEqual(SigmaRuleChangeResult.Conflict,
                await management.SetAsync(source.Id, "rules/example.yml", saved.TestId, true, "", actor, token));
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", null, false, version, actor, token));
            version = Convert.ToBase64String((await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", saved.TestId, true, version, actor, token));
            version = Convert.ToBase64String((await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Version);
            await using (var check = contexts.CreateDbContext())
            await using (var transaction = await check.Database.BeginTransactionAsync(token))
                Assert.IsFalse(await SigmaEnabledRules.StillCurrentAsync(check, new SigmaCatalog(contexts, clock), loaded, token));
            reader.Events = [positive];
            Assert.AreEqual(SigmaTestingStatus.EventUnavailable, (await testing.RunAsync("rules/example.yml", samples, Guid.CreateVersion7(), token)).Status);
            reader.Events = [positive, negative];
            reader.BeforeReturn = async cancellation =>
            {
                await using var change = contexts.CreateDbContext();
                (await change.IntelligenceSources.SingleAsync(cancellation)).Enabled = false;
                await change.SaveChangesAsync(cancellation);
            };
            Assert.AreEqual(SigmaTestingStatus.SourceUnavailable, (await testing.RunAsync("rules/example.yml", samples, Guid.CreateVersion7(), token)).Status);
            var retained = Assert.ContainsSingle((await inventory.GetStatesAsync(1, token)).Items);
            Assert.IsTrue(retained.Enabled);
            Assert.AreEqual(version, retained.Version);
            Assert.AreEqual(SigmaRuleChangeResult.Changed,
                await management.SetAsync(source.Id, "rules/example.yml", null, false, version, actor, token));
            Assert.IsFalse((await db.SigmaRuleActivations.AsNoTracking().SingleAsync(token)).Enabled);
            await using (var check = contexts.CreateDbContext())
            await using (var transaction = await check.Database.BeginTransactionAsync(token))
                Assert.IsFalse(await SigmaEnabledRules.StillCurrentAsync(check, new SigmaCatalog(contexts, clock), loaded, token));
            var audits = await new OperationAuditQueryService(new OperationsFactory(operations))
                .QueryIntelligenceAsync(new(clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow().AddMinutes(1)), token);
            Assert.HasCount(6, audits.Items);
            Assert.AreEqual(4, audits.Items.Count(x => x.SigmaRuleState is { Enabled: true }));
            Assert.AreEqual(2, audits.Items.Count(x => x.SigmaRuleState is { Enabled: false }));
            Assert.AreEqual(3, await db.SigmaRuleTests.CountAsync(token));
            for (var i = 0; i < 51; i++)
                db.SigmaDetectionResults.Add(new SigmaDetectionResult
                {
                    AgentId = agent, DeviceId = i == 50 ? Guid.CreateVersion7() : dispatch.DeviceId, DeviceGeneration = 1,
                    MessageId = Guid.CreateVersion7(), ActivationId = detection.ActivationId, TestId = detection.TestId,
                    ActivationVersion = detection.ActivationVersion,
                    Result = (int)SigmaMatch.Yes, OccurredAt = clock.GetUtcNow(), ReceivedAt = clock.GetUtcNow(), EvaluatedAt = clock.GetUtcNow()
                });
            await db.SaveChangesAsync(token);
            var firstHistoryPage = await history.GetEventsAsync(dispatch.DeviceId, historyFrom, historyUntil, null, token);
            Assert.IsNotNull(firstHistoryPage);
            Assert.HasCount(50, firstHistoryPage.Items);
            Assert.IsNotNull(firstHistoryPage.NextCursor);
            var nextHistoryPage = await history.GetEventsAsync(dispatch.DeviceId, historyFrom, historyUntil, firstHistoryPage.NextCursor, token);
            Assert.IsNotNull(nextHistoryPage);
            Assert.HasCount(1, nextHistoryPage.Items);
            Assert.IsNull(nextHistoryPage.NextCursor);
            Assert.AreEqual(51, firstHistoryPage.Items.Concat(nextHistoryPage.Items).Select(x => x.MessageId).Distinct().Count());
        }, context.CancellationToken);

    [TestMethod]
    public Task DetectionMigration_RetainsLongPathsAndRefusesToDiscardTests() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "sigmahq-rules", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            var actor = Guid.CreateVersion7();
            var path = new string('a', 500);
            var test = new SigmaRuleTest { Id = Guid.CreateVersion7(), SourceId = source.Id, RevisionId = revision.Id,
                Path = path, Digest = new string('a', 64), ActivationVersion = "AQ==", MatcherVersion = "1",
                SamplesJson = "[]", Passed = true, ActorId = actor, TestedAt = DateTimeOffset.UtcNow };
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            db.SigmaRuleTests.Add(test);
            db.SigmaRuleActivations.Add(new SigmaRuleActivation { Id = Guid.CreateVersion7(), SourceId = source.Id, Path = path,
                TestId = test.Id, Enabled = true, UpdatedBy = actor, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
            Assert.AreEqual(path, (await db.SigmaRuleActivations.SingleAsync(token)).Path);
            var target = db.Database.GetMigrations().TakeWhile(x => !x.EndsWith("_SigmaDetection", StringComparison.Ordinal)).Last();
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.SigmaRuleTests.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Migration_RefusesToDiscardRetainedRules() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var source = new IntelligenceSource { Key = "sigmahq-rules", Enabled = true };
            var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = First, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
            db.IntelligenceSources.Add(source);
            db.IntelligenceRevisions.Add(revision);
            db.SigmaRules.Add(new SigmaRule { RevisionId = revision.Id, Path = "rules/example.yml", Title = "Example", Digest = new byte[32], Content = [1] });
            db.SigmaManifests.Add(new SigmaManifest { RevisionId = revision.Id, PolicyVersion = "test", IncludedCount = 1, Digest = new byte[32] });
            await db.SaveChangesAsync(token);
            var target = db.Database.GetMigrations().Single(x => x.EndsWith("_IntelligenceSourceLimits", StringComparison.Ordinal));
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync(target, token));
            Assert.AreEqual(51000, error.Number);
            Assert.AreEqual(1, await db.SigmaRules.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_DiscardsDownloadWhenPolicyChanges() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                using var db = contexts.CreateDbContext();
                db.IntelligenceSources.Single().MaximumRecords = 1;
                db.SaveChanges();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, invalid: false, wrongLicense: false)) };
            });
            var result = await new SigmaRefresh(contexts, clients, TimeProvider.System).RunDueAsync(token);
            Assert.AreEqual(SigmaRefreshResult.Conflict, result);
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(1, (await check.IntelligenceSources.SingleAsync(token)).MaximumRecords);
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await check.SigmaRules.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Refresh_CancellationDoesNotPublishPartialData() =>
        ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString).Options;
            var contexts = new Factory(options);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var clients = new ClientFactory(request =>
            {
                if (request.RequestUri!.Host == "api.github.com") return new(HttpStatusCode.OK) { Content = new StringContent($"{{\"sha\":\"{First}\"}}") };
                cancellation.Cancel();
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive(First, invalid: false, wrongLicense: false)) };
            });
            await Assert.ThrowsAsync<OperationCanceledException>(() => new SigmaRefresh(contexts, clients, TimeProvider.System).RunDueAsync(cancellation.Token));
            await using var check = contexts.CreateDbContext();
            Assert.AreEqual(0, await check.IntelligenceRevisions.CountAsync(token));
            Assert.AreEqual(0, await check.SigmaRules.CountAsync(token));
            Assert.IsNull((await check.IntelligenceSources.SingleAsync(token)).LastError);
        }, context.CancellationToken);

    [TestMethod]
    public async Task Archive_RejectsUnreviewedNestedLicense()
    {
        using var archive = new MemoryStream(Archive(First, invalid: false, wrongLicense: false, nestedLicense: true));
        await Assert.ThrowsAsync<SigmaLicenseException>(() => SigmaArchive.ReadAsync(archive, First, 50_000,
            (_, _, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    private static byte[] Archive(string commit, bool invalid, bool wrongLicense, bool nestedLicense = false, int extraRules = 0, string? ruleContent = null, string? correlationContent = null, string? additionalRuleContent = null)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new("comment", commit)]));
            Write($"sigma-{commit}/LICENSE", wrongLicense ? "changed" : "# Licenses\n\nThe content of this repository is released under the following licenses:\n\n- The Sigma specification (https://github.com/SigmaHQ/sigma-specification) and the Sigma logo are public domain\n- The rules contained in the SigmaHQ repository (https://github.com/SigmaHQ) are released under the [Detection Rule License (DRL) 1.1](https://github.com/SigmaHQ/Detection-Rule-License)\n");
            Write($"sigma-{commit}/rules/example.yml", ruleContent ?? "title: Example\nid: abc\nauthor: Author\nlogsource: {product: windows}\ndetection: {condition: selection}\n");
            if (additionalRuleContent is not null) Write($"sigma-{commit}/rules/second.yml", additionalRuleContent);
            if (correlationContent is not null) Write($"sigma-{commit}/rules/correlation.yml", correlationContent);
            Write($"sigma-{commit}/rules-threat-hunting/other.yml", "title: Hunting\nlogsource: {product: linux}\ndetection: {condition: selection}\n");
            for (var i = 0; i < extraRules; i++) Write($"sigma-{commit}/rules/extra-{i:D3}.yml", "title: Extra\nauthor: Author\nlogsource: {}\ndetection: {}\n");
            if (nestedLicense) Write($"sigma-{commit}/rules/windows/LICENSE", "different terms");
            if (invalid)
            {
                for (var i = 0; i < 18; i++) Write($"sigma-{commit}/rules/extra-{i}.yml", "title: Extra\nlogsource: {}\ndetection: {}\n");
                Write($"sigma-{commit}/rules/../bad.yml", "bad");
            }
            void Write(string name, string text)
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = content });
            }
        }
        return output.ToArray();
    }

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    { public SecurityAnalysisDbContext CreateDbContext() => new(options); }
    private sealed class OperationsFactory(DbContextOptions<OperationsDbContext> options) : IDbContextFactory<OperationsDbContext>
    { public OperationsDbContext CreateDbContext() => new(options); }
    private sealed class DetectionDeviceSource(Guid id) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token) =>
            Task.FromResult(deviceId == id ? new DeviceAgentState(id, 1, true) : null);
        public Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            throw new NotSupportedException();
    }
    private sealed class RejectAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated audit failure.");
    }
    private sealed class ReplayLogReader(IReadOnlyList<HostLogEntry> events) : IHostLogReader
    {
        public IReadOnlyList<HostLogEntry> Events { get; set; } = events;
        public Func<CancellationToken, Task>? BeforeReturn { get; set; }
        public async Task<IReadOnlyList<HostLogEntry>> ReadAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token)
        {
            if (BeforeReturn is not null) await BeforeReturn(token);
            return Events.Where(x => x.AgentId == agentId && messageIds.Contains(x.MessageId)).ToArray();
        }
    }
    private sealed class DetectionTimeline : IDetectionTimelineWriter
    {
        public List<EventTimelineEntry> Items { get; } = [];
        public Task WriteAsync(SecurityAnalysisDbContext db, IReadOnlyList<EventTimelineEntry> entries, CancellationToken token)
        { Items.AddRange(entries); return Task.CompletedTask; }
    }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan value) => now += value; }
    private sealed class AdvancingClock(DateTimeOffset now) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref calls) == 1 ? now : now.AddDays(31);
    }
    private sealed class ClientFactory(Func<HttpRequestMessage, HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
    }
}
