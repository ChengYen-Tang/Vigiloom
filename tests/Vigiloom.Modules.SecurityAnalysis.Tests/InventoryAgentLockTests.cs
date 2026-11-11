using Microsoft.Data.SqlClient;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventoryAgentLockTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Lock_SerializesOneAgentWithoutBlockingAnother() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            await using var first = new SqlConnection(database.ConnectionString);
            await using var second = new SqlConnection(database.ConnectionString);
            await first.OpenAsync(token);
            await second.OpenAsync(token);
            await using var firstTransaction = await first.BeginTransactionAsync(token);
            await using var secondTransaction = await second.BeginTransactionAsync(token);
            var agent = Guid.CreateVersion7();
            Assert.IsTrue(await InventoryAgentLock.TryAcquireAsync(first, firstTransaction, agent, 0, token));
            Assert.IsFalse(await InventoryAgentLock.TryAcquireAsync(second, secondTransaction, agent, 0, token));
            Assert.IsTrue(await InventoryAgentLock.TryAcquireAsync(second, secondTransaction, Guid.CreateVersion7(), 0, token));
            await firstTransaction.RollbackAsync(token);
            Assert.IsTrue(await InventoryAgentLock.TryAcquireAsync(second, secondTransaction, agent, 0, token));
            await secondTransaction.RollbackAsync(token);
        }, TestContext.CancellationToken);
}
