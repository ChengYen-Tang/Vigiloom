using Microsoft.Data.SqlClient;
using Vigiloom.Infrastructure.LogAdmission;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;

namespace Vigiloom.Infrastructure.Tests.Database;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ReusableLogQuotaDatabaseTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reset_ClearsAdmissionBucketsInEitherOrder(bool reverse)
    {
        var kinds = reverse ? new[] { LogSourceKind.Application, LogSourceKind.Agent } : new[] { LogSourceKind.Agent, LogSourceKind.Application };
        foreach (var kind in kinds)
            await ReusableLogQuotaDatabase.ExecuteAsync(async (database, token) =>
            {
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT COUNT(*) FROM [logquota].[Buckets]) + (SELECT COUNT(*) FROM [logquota].[Policies])";
                Assert.AreEqual(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT COUNT(*) FROM [logquota].[__EFMigrationsHistory]";
                Assert.IsGreaterThan(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
                await using var transaction = await connection.BeginTransactionAsync(token);
                var outcome = await Admission.AdmitAsync(connection, transaction, new LogSource(kind, Guid.CreateVersion7()), 1, token);
                Assert.AreEqual(LogAdmissionStatus.Accepted, outcome.Status);
                await transaction.CommitAsync(token);
            }, context.CancellationToken);
    }
}
