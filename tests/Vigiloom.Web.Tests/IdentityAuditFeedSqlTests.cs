using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IdentityAuditFeedSqlTests(TestContext testContext)
{
    [TestMethod]
    public Task Query_UsesStablePagesAndHidesUnknownActions() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var services = new ServiceCollection();
        services.AddModuleSqlServer<IdentityAccessDbContext>(database.ConnectionString, "identity");
        await using var provider = services.BuildServiceProvider();
        var contexts = provider.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var subjects = Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7()).ToArray();
        var actor = Guid.CreateVersion7();
        Guid?[] expected;
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            await db.Database.MigrateAsync(token);
            foreach (var subject in subjects) db.AccountAuditEvents.Add(new AccountAuditEvent(actor, subject, "AccountCreated", timestamp));
            db.AccountAuditEvents.Add(new AccountAuditEvent(null, Guid.CreateVersion7(), "secret-unapproved-action", timestamp.AddMinutes(1)));
            db.ApiCredentialAuditEvents.Add(new ApiCredentialAuditEvent(actor, Guid.CreateVersion7(), "ApiCredentialIssued", timestamp));
            await db.SaveChangesAsync(token);
            expected = await db.AccountAuditEvents.AsNoTracking().Where(x => x.OccurredAt == timestamp)
                .OrderByDescending(x => x.RowId).Select(x => (Guid?)x.SubjectId).ToArrayAsync(token);
            var indexes = await db.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS [Value] FROM sys.indexes
                WHERE (object_id = OBJECT_ID('identity.AccountAuditEvents') AND name = 'IX_AccountAuditEvents_OccurredAt_RowId')
                   OR (object_id = OBJECT_ID('identity.ApiCredentialAuditEvents') AND name = 'IX_ApiCredentialAuditEvents_OccurredAt_RowId')
                """).SingleAsync(token);
            Assert.AreEqual(2, indexes);
        }
        var feed = new IdentityAuditFeed(contexts);
        var range = new OperationAuditQuery(timestamp.AddHours(-1), timestamp.AddHours(1));
        var first = await feed.QueryAsync(AuditFeedSource.Accounts, range, token);
        Assert.HasCount(50, first.Items);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(AuditFeedAction.Unknown, first.Items[0].Action);
        Assert.IsNull(first.Items[0].ActorId);
        var second = await feed.QueryAsync(AuditFeedSource.Accounts, range with { Page = 2 }, token);
        Assert.HasCount(2, second.Items);
        Assert.IsFalse(second.HasMore);
        var actual = first.Items.Skip(1).Concat(second.Items).Select(item => item.SubjectId).ToArray();
        CollectionAssert.AreEqual(expected, actual);
        var credentials = await feed.QueryAsync(AuditFeedSource.ApiCredentials, range, token);
        Assert.HasCount(1, credentials.Items);
        Assert.AreEqual(AuditFeedAction.ApiCredentialIssued, credentials.Items[0].Action);
        await Assert.ThrowsAsync<ArgumentException>(() => feed.QueryAsync(AuditFeedSource.Devices, range, token));
        await Assert.ThrowsAsync<ArgumentException>(() => feed.QueryAsync(AuditFeedSource.Accounts, range with { Page = 0 }, token));
    }, testContext.CancellationToken);
}
