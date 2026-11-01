using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Infrastructure.Tests;

[TestClass]
public sealed class SqlServerRegistrationTests
{
    [TestMethod]
    public void AddModuleSqlServer_FactoryCreatesIndependentContexts_WithSqlServerProvider()
    {
        var services = new ServiceCollection();
        services.AddModuleSqlServer<FactoryTestContext>(
            "Server=localhost;Database=VigiloomTests;Integrated Security=true;TrustServerCertificate=true");

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var factory = provider.GetRequiredService<IDbContextFactory<FactoryTestContext>>();

        using var first = factory.CreateDbContext();
        using var second = factory.CreateDbContext();

        Assert.AreNotSame(first, second);
        Assert.AreEqual("Microsoft.EntityFrameworkCore.SqlServer", first.Database.ProviderName);
        Assert.AreEqual("VigiloomTests", first.Database.GetDbConnection().Database);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void AddModuleSqlServer_EmptyConnectionString_IsRejected(string? connectionString)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentException>(
            () => services.AddModuleSqlServer<FactoryTestContext>(connectionString!));

        Assert.AreEqual("connectionString", exception.ParamName);
    }

    // This context has no product model and never opens a database connection.
    public sealed class FactoryTestContext(DbContextOptions<FactoryTestContext> options) : DbContext(options);
}
