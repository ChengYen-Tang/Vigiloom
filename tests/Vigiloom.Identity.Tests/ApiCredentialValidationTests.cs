using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Identity.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ApiCredentialValidationTests
{
    public TestContext TestContext { get; set; } = null!;

    [AssemblyCleanup]
    public static void CleanupAssembly() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task Validate_ReflectsCredentialRevocation() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:VigiloomIdentity"] = database.ConnectionString })));
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            var secret = RandomNumberGenerator.GetBytes(32);
            var credential = new ApiCredential("integration", SHA256.HashData(secret), true, DateTimeOffset.UtcNow.AddDays(1), Guid.CreateVersion7());
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.ApiCredentials.Add(credential);
                await db.SaveChangesAsync(token);
            }
            var value = $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(secret)}";
            using var client = factory.CreateClient();
            using (var response = await client.PostAsJsonAsync("/internal/identity/api-credentials/validate", new VerifyApiCredentialRequest(value), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore ?? false);
                var result = await response.Content.ReadFromJsonAsync<VerifiedApiCredentialResponse>(cancellationToken: token);
                Assert.AreEqual(credential.Id, result!.Id);
                Assert.IsTrue(result.CanWrite);
            }
            using (var uppercase = await client.PostAsJsonAsync("/internal/identity/api-credentials/validate", new VerifyApiCredentialRequest("VLG_" + value[4..]), token))
                Assert.AreEqual(HttpStatusCode.OK, uppercase.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var stored = await db.ApiCredentials.SingleAsync(x => x.Id == credential.Id, token);
                stored.Revoke(DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            using var revoked = await client.PostAsJsonAsync("/internal/identity/api-credentials/validate", new VerifyApiCredentialRequest(value), token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, TestContext.CancellationToken);
}
