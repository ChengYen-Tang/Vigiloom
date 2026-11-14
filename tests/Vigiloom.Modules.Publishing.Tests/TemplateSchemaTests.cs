using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.Publishing.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class TemplateSchemaTests(TestContext context)
{
    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Migration_PreservesActiveAndRetiredTemplates(bool retired) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "publishing")).Options;
            await using var db = new PublishingDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("0", token);
            await db.Database.MigrateAsync(token);
            var template = new PublicationTemplate("Example", true, false, DateTimeOffset.UtcNow);
            Assert.AreEqual(7, template.Id.Version);
            if (retired) template.Retire(DateTimeOffset.UtcNow);
            db.Templates.Add(template);
            await db.SaveChangesAsync(token);
            var failure = await Assert.ThrowsExactlyAsync<SqlException>(() => migrator.MigrateAsync("0", token));
            Assert.AreEqual(51004, failure.Number);
            Assert.AreEqual(1, await db.Templates.CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task Schema_EnforcesDefaultAndActiveNameUniqueness() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<PublishingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "publishing")).Options;
            await using var db = new PublishingDbContext(options);
            await db.Database.MigrateAsync(token);
            var primary = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'[publishing].[Templates]') AND i.is_primary_key = 1 AND i.type = 2 AND c.name = N'Id'
                """).SingleAsync(token);
            var clustered = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'[publishing].[Templates]') AND i.is_unique = 1 AND i.type = 1 AND c.name = N'RowId'
                """).SingleAsync(token);
            Assert.AreEqual(1, primary);
            Assert.AreEqual(1, clustered);
            var now = DateTimeOffset.UtcNow;
            var first = new PublicationTemplate("Example", true, true, now);
            first.SetDefault(true, now);
            db.Templates.Add(first);
            await db.SaveChangesAsync(token);
            Assert.IsGreaterThan(0, first.RowId);
            Assert.HasCount(8, first.Version);
            var duplicate = new PublicationTemplate("example", false, false, now);
            db.Templates.Add(duplicate);
            var duplicateError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            Assert.IsInstanceOfType<SqlException>(duplicateError.InnerException);
            Assert.IsTrue(((SqlException)duplicateError.InnerException).Number is 2601 or 2627);
            db.Entry(duplicate).State = EntityState.Detached;
            var other = new PublicationTemplate("Other", false, false, now);
            other.SetDefault(true, now);
            db.Templates.Add(other);
            var defaultError = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            Assert.IsInstanceOfType<SqlException>(defaultError.InnerException);
            Assert.IsTrue(((SqlException)defaultError.InnerException).Number is 2601 or 2627);
            db.Entry(other).State = EntityState.Detached;
            first.Retire(now);
            await db.SaveChangesAsync(token);
            var replacement = new PublicationTemplate("example", false, false, now);
            db.Templates.Add(replacement);
            await db.SaveChangesAsync(token);
            var retiredDefault = await Assert.ThrowsExactlyAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [publishing].[Templates] SET [IsDefault] = 1 WHERE [Id] = {first.Id}", token));
            Assert.AreEqual(547, retiredDefault.Number);
        }, context.CancellationToken);
}
