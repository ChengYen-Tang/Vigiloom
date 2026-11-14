using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class IntelligenceHttpTests(TestContext context)
{
    [TestMethod]
    public Task Management_RequiresAdministratorAndAntiforgery_AndNeverReturnsArtifact() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
            });
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var advisorySource = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            var secret = "SECRET-ARTIFACT-MUST-NOT-BE-RETURNED";
            var revision = new IntelligenceRevision
            {
                SourceId = source.Id, UpstreamVersion = "v1", ReleasedAt = DateTimeOffset.UtcNow, RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = System.Text.Encoding.UTF8.GetBytes(secret),
                SchemaVersion = "a795af4c4afc47df12906b4c04e656156cf06493", MappingVersion = "kev-cve-v1",
                LicenseEvidence = "CISA KEV: CC0-1.0; cisagov/kev-data@a795af4c4afc47df12906b4c04e656156cf06493", RecordCount = 1, Normalized = true
            };
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            {
                db.IntelligenceSources.Add(source);
                db.IntelligenceSources.Add(advisorySource);
                db.IntelligenceRevisions.Add(revision);
                await db.SaveChangesAsync(token);
                db.AdvisoryImports.Add(new AdvisoryImport { SourceId = advisorySource.Id, SourceVersion = advisorySource.Version,
                    WindowStart = new(2023, 1, 1), WindowEnd = new(2023, 2, 1), StartedAt = DateTimeOffset.UtcNow,
                    Cursor = "SECRET-CURSOR", FailureCode = "fetch-failed" });
                await db.SaveChangesAsync(token);
            }
            string apiCredential;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var account = await scope.ServiceProvider.GetRequiredService<UserManager<Account>>().FindByNameAsync("admin");
                Assert.IsNotNull(account);
                var bytes = RandomNumberGenerator.GetBytes(32);
                var credential = new ApiCredential("test", SHA256.HashData(bytes), true, DateTimeOffset.UtcNow.AddDays(1), account.Id);
                var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
                db.ApiCredentials.Add(credential);
                await db.SaveChangesAsync(token);
                apiCredential = $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
            }
            using var anonymous = PingTestSite.Client(factory);
            using var reader = PingTestSite.Client(factory);
            using var admin = PingTestSite.Client(factory);
            using var api = PingTestSite.Client(factory);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiCredential);
            await PingTestSite.LoginAsync(reader, "reader", token);
            await PingTestSite.LoginAsync(admin, "admin", token);
            const string path = "/api/v1/admin/intelligence/sources/";
            using (var result = await anonymous.GetAsync(path, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await reader.GetAsync(path, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await api.GetAsync(path, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await admin.GetAsync(path, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                Assert.IsTrue(result.Headers.CacheControl?.NoStore);
                var body = await result.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(secret, body);
            }
            using (var result = await admin.GetAsync($"{path}{source.Id:D}/revisions", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                Assert.DoesNotContain(secret, await result.Content.ReadAsStringAsync(token));
            }
            var progressPath = $"{path}{advisorySource.Id:D}/advisory-progress";
            AdvisoryImportProgressView? advisoryProgress = null;
            using (var result = await anonymous.GetAsync(progressPath, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await reader.GetAsync(progressPath, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await api.GetAsync(progressPath, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await admin.GetAsync(progressPath, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                Assert.IsTrue(result.Headers.CacheControl?.NoStore);
                var body = await result.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("SECRET-CURSOR", body);
                advisoryProgress = JsonSerializer.Deserialize<AdvisoryImportProgressView>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.IsNotNull(advisoryProgress);
                Assert.AreEqual("failed", advisoryProgress.State);
                Assert.AreEqual("fetch-failed", advisoryProgress.FailureCode);
            }
            using (var result = await admin.GetAsync($"{path}{source.Id:D}/advisory-progress", token)) Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
            using (var result = await admin.GetAsync($"{path}?page=0", token)) Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
            using (var result = await admin.GetAsync($"{path}{Guid.CreateVersion7():D}/revisions", token)) Assert.AreEqual(HttpStatusCode.NotFound, result.StatusCode);
            var request = new IntelligenceActivationRequest(revision.Id, null, "", false);
            using (var result = await admin.PostAsJsonAsync($"{path}{source.Id:D}/activation", request, token)) Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.IsNotNull(advisoryProgress?.ImportId);
            Assert.IsNotNull(advisoryProgress.Version);
            var recovery = new AdvisoryImportRecoveryRequest(advisoryProgress.ImportId.Value, advisoryProgress.Version);
            var recoveryPath = $"{path}{advisorySource.Id:D}/advisory-recovery";
            var credentialPath = $"{path}{advisorySource.Id:D}/advisory-credential";
            var credentialUpdate = new AdvisoryCredentialUpdate(Convert.ToBase64String(advisorySource.Version), false, "sensitive-github-token");
            using (var result = await anonymous.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await reader.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await api.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await admin.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
            using (var result = await anonymous.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await reader.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await api.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            using (var result = await admin.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            using (var json = JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token)))
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
            using (var result = await admin.PostAsJsonAsync(recoveryPath, recovery with { Version = Convert.ToBase64String(new byte[8]) }, token)) Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            using (var result = await admin.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            using (var result = await admin.PostAsJsonAsync(recoveryPath, recovery, token)) Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            using (var result = await admin.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            using (var result = await admin.PutAsJsonAsync(credentialPath, credentialUpdate, token)) Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            using (var result = await admin.GetAsync(path, token))
            {
                var body = await result.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("sensitive-github-token", body);
                var items = JsonSerializer.Deserialize<IntelligenceSourcePage>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.IsTrue(items?.Items.Single(x => x.Id == advisorySource.Id).HasCredential);
            }
            using (var result = await admin.PostAsJsonAsync($"{path}{source.Id:D}/activation", request, token)) Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            using (var result = await admin.PostAsJsonAsync($"{path}{source.Id:D}/activation", request, token)) Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            var sourcePage = await admin.GetFromJsonAsync<IntelligenceSourcePage>(path, token);
            Assert.IsNotNull(sourcePage);
            var current = sourcePage.Items.Single(x => x.Id == source.Id);
            var policy = new IntelligencePolicyUpdate(true, 43_200, current.StaleAfterSeconds, current.MaximumBytes, current.MaximumRecords, current.Version);
            using (var result = await admin.PutAsJsonAsync($"{path}{source.Id:D}/policy", policy with { MaximumBytes = 67_108_865 }, token)) Assert.AreEqual(HttpStatusCode.BadRequest, result.StatusCode);
            using (var result = await admin.PutAsJsonAsync($"{path}{source.Id:D}/policy", policy, token)) Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            using (var result = await admin.PutAsJsonAsync($"{path}{source.Id:D}/policy", policy, token)) Assert.AreEqual(HttpStatusCode.Conflict, result.StatusCode);
            using (var result = await reader.PutAsJsonAsync($"{path}{source.Id:D}/policy", policy, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await api.PutAsJsonAsync($"{path}{source.Id:D}/policy", policy, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, result.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var auditPath = $"/api/v1/operations/audit/intelligence?from={Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"))}&to={Uri.EscapeDataString(now.AddMinutes(1).ToString("O"))}";
            using (var result = await reader.GetAsync(auditPath, token)) Assert.AreEqual(HttpStatusCode.Forbidden, result.StatusCode);
            using (var result = await admin.GetAsync(auditPath, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
                var events = await result.Content.ReadFromJsonAsync<OperationAuditPage>(token);
                Assert.IsNotNull(events);
                Assert.IsTrue(events.Items.Any(x => x.IntelligenceRevision is not null));
                Assert.IsTrue(events.Items.Any(x => x.IntelligencePolicy is not null));
                Assert.IsTrue(events.Items.Any(x => x.AdvisoryImport?.ImportId == recovery.ImportId));
                Assert.IsTrue(events.Items.Any(x => x.AdvisoryCredential?.Configured == true));
            }
        }, context.CancellationToken);
}
