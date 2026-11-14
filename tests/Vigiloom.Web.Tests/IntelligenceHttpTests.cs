using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
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
    public Task OfflineImport_RequiresAdministratorAndAntiforgery_AndShowsProvenance() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
            });
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            using var admin = PingTestSite.Client(factory);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(admin, "admin", token);
            await PingTestSite.LoginAsync(reader, "reader", token);
            var pageUrl = $"/settings/intelligence?source={source.Id:D}";
            var uploadUrl = $"/settings/intelligence/{source.Id:D}/offline-import";
            using var page = await admin.GetAsync(pageUrl, token);
            var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
            var form = document.QuerySelector($"form[action='{uploadUrl}']");
            Assert.IsNotNull(form);
            var csrf = form.QuerySelector("input[name='__RequestVerificationToken']")?.GetAttribute("value");
            Assert.IsFalse(string.IsNullOrEmpty(csrf));
            var payload = System.Text.Encoding.UTF8.GetBytes($"{{\"catalogVersion\":\"offline-test\",\"dateReleased\":\"{DateTimeOffset.UtcNow:O}\",\"count\":0,\"vulnerabilities\":[]}}");
            MultipartFormDataContent Upload(string? tokenValue, bool includeFile = true)
            {
                var content = new MultipartFormDataContent();
                if (tokenValue is not null) content.Add(new StringContent(tokenValue), "__RequestVerificationToken");
                if (includeFile) content.Add(new ByteArrayContent(payload), "file", "catalog.json");
                return content;
            }
            using (var content = Upload(null)) using (var response = await admin.PostAsync(uploadUrl, content, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using (var content = Upload(csrf, false)) using (var response = await admin.PostAsync(uploadUrl, content, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using (var content = Upload(csrf)) using (var response = await reader.PostAsync(uploadUrl, content, token))
                Assert.IsFalse(response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location?.ToString().Contains("import=saved", StringComparison.Ordinal) == true);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
                Assert.AreEqual(0, await db.OfflineImports.CountAsync(token));
            using (var content = Upload(csrf)) using (var response = await admin.PostAsync(uploadUrl, content, token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
                Assert.Contains("import=saved", response.Headers.Location!.ToString());
            }
            using var imported = await admin.GetAsync(pageUrl, token);
            var importedDoc = await new HtmlParser().ParseDocumentAsync(await imported.Content.ReadAsStringAsync(token), token);
            Assert.Contains("offline-test", importedDoc.Body!.TextContent);
            Assert.Contains("管理員離線匯入", importedDoc.Body.TextContent);
            Assert.Contains("未驗證上游身分", importedDoc.Body.TextContent);
            Guid revisionId;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
                revisionId = (await db.IntelligenceRevisions.SingleAsync(token)).Id;
            var activateForm = importedDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-activation");
            var activate = activateForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            activate["ActivationInput.RevisionId"] = revisionId.ToString("D");
            activate["ActivationInput.ConfirmReduction"] = "true";
            using (var noConfirmation = await admin.PostAsync(pageUrl, new FormUrlEncodedContent(activate), token))
                Assert.Contains("請確認離線檔案來源", (await new HtmlParser().ParseDocumentAsync(await noConfirmation.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            activate["ActivationInput.ConfirmOffline"] = "true";
            using (var confirmed = await admin.PostAsync(pageUrl, new FormUrlEncodedContent(activate), token))
                Assert.Contains("版本已啟用", (await new HtmlParser().ParseDocumentAsync(await confirmed.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            using var audit = await admin.GetAsync("/settings/audit?source=intelligence", token);
            var auditText = (await new HtmlParser().ParseDocumentAsync(await audit.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("離線匯入情報", auditText);
            Assert.Contains("管理員已確認離線來源與使用條件", auditText);
        }, context.CancellationToken);

    [TestMethod]
    public Task ManagementPage_ActivatesVersionAndProtectsCredential() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
            });
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var kev = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
            var gh = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
            var sigma = new IntelligenceSource { Key = "sigmahq-rules", Enabled = true };
            var signature = new IntelligenceSource { Key = "signature-base", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow,
                LastError = "Signature-Base license unavailable or changed." };
            var revision = new IntelligenceRevision { SourceId = kev.Id, UpstreamVersion = "v-test", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "a795af4c4afc47df12906b4c04e656156cf06493", MappingVersion = "kev-cve-v1",
                LicenseEvidence = "CISA KEV: CC0-1.0; cisagov/kev-data@a795af4c4afc47df12906b4c04e656156cf06493", RecordCount = 1, Normalized = true };
            var secondRevision = new IntelligenceRevision { SourceId = kev.Id, UpstreamVersion = "v-test-2", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = Enumerable.Repeat((byte)1, 32).ToArray(), Artifact = [], SchemaVersion = revision.SchemaVersion, MappingVersion = revision.MappingVersion,
                LicenseEvidence = revision.LicenseEvidence, RecordCount = 1, Normalized = true };
            var sigmaRevision = new IntelligenceRevision { SourceId = sigma.Id, UpstreamVersion = "test-sigma-commit", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "sigma-yaml-source-v1", MappingVersion = "sigma-rule-source-v2",
                LicenseEvidence = "SigmaHQ rules: DRL-1.1, subject to individual rule terms; SigmaHQ/sigma@54967d64e6dda2b4daa81f47f7a8bf1f591b68a0", RecordCount = 1, Normalized = true };
            var rule = new SigmaRule { RevisionId = sigmaRevision.Id, Path = "rules/windows/example.yml", RuleId = "sample-rule", Title = "Example behavior",
                Author = "Example Author", Product = "windows", Digest = new byte[32], Content = System.Text.Encoding.UTF8.GetBytes("SECRET-RULE-CONTENT") };
            var signatureRevision = new IntelligenceRevision { SourceId = signature.Id, UpstreamVersion = "signature-test", RetrievedAt = DateTimeOffset.UtcNow,
                Digest = new byte[32], Artifact = [], SchemaVersion = "signature-base-files-v1", MappingVersion = "signature-base-source-v2",
                LicenseEvidence = "Signature-Base repository terms and per-rule exceptions require review; Neo23x0/signature-base@eb8c53cb08ed103d031fe9c98d1d23e94904839d", RecordCount = 2, Normalized = true };
            var signatureFile = new SignatureFile { RevisionId = signatureRevision.Id, Path = "iocs/hash-iocs.txt", Kind = 0,
                Digest = new byte[32], Content = System.Text.Encoding.UTF8.GetBytes("SECRET-IOC-CONTENT") };
            var yaraBytes = System.Text.Encoding.UTF8.GetBytes("rule example {\n meta:\n author = \"<script>alert(1)</script>\"\n license = \"Example\"\n condition: true\n}");
            var yaraFile = new SignatureFile { RevisionId = signatureRevision.Id, Path = "yara/example.yar", Kind = 1,
                Digest = SHA256.HashData(yaraBytes), Content = yaraBytes };
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            {
                db.IntelligenceSources.AddRange(kev, gh, sigma, signature); db.IntelligenceRevisions.AddRange(revision, secondRevision, sigmaRevision, signatureRevision);
                db.SigmaRules.Add(rule); db.SignatureFiles.AddRange(signatureFile, yaraFile); await db.SaveChangesAsync(token);
                db.IocEntries.Add(new IocEntry { SignatureFileRowId = signatureFile.RowId, LineNumber = 1, Kind = "unsupported", Reason = "invalid-hash" });
                await db.SaveChangesAsync(token);
            }
            using var admin = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(admin, "admin", token);
            using (var sourcePage = await admin.GetAsync($"/settings/intelligence?source={sigma.Id:D}", token))
            {
                var body = await sourcePage.Content.ReadAsStringAsync(token);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("Example behavior", doc.Body!.TextContent);
                Assert.Contains("Example Author", doc.Body.TextContent);
                Assert.Contains("windows", doc.Body.TextContent);
                Assert.Contains("不會部署規則或啟用偵測", doc.Body.TextContent);
                Assert.DoesNotContain("SECRET-RULE-CONTENT", body);
                Assert.IsTrue(doc.QuerySelectorAll("form").Any(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-activation"));
            }
            using (var rulesResponse = await admin.GetAsync($"/api/v1/admin/intelligence/sources/{sigma.Id:D}/revisions/{sigmaRevision.Id:D}/sigma-rules", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, rulesResponse.StatusCode);
                var body = await rulesResponse.Content.ReadAsStringAsync(token);
                Assert.Contains("Example behavior", body);
                Assert.DoesNotContain("SECRET-RULE-CONTENT", body);
            }
            using (var signaturePage = await admin.GetAsync($"/settings/intelligence?source={signature.Id:D}", token))
            {
                var body = await signaturePage.Content.ReadAsStringAsync(token);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("iocs/hash-iocs.txt", doc.Body!.TextContent);
                Assert.Contains("1 筆未支援", doc.Body.TextContent);
                Assert.Contains("必須重新確認授權", doc.Body.TextContent);
                Assert.Contains("消費端相容性尚未驗證", doc.Body.TextContent);
                Assert.DoesNotContain("SECRET-IOC-CONTENT", body);
            }
            using (var response = await admin.GetAsync($"/api/v1/admin/intelligence/sources/{signature.Id:D}/revisions/{signatureRevision.Id:D}/signature-files", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(token);
                Assert.Contains("iocs/hash-iocs.txt", body);
                Assert.DoesNotContain("SECRET-IOC-CONTENT", body);
            }
            using (var evidencePage = await admin.GetAsync($"/settings/intelligence?source={signature.Id:D}&rulesRevision={signatureRevision.Id:D}&evidencePath=yara%2Fexample.yar", token))
            {
                var body = await evidencePage.Content.ReadAsStringAsync(token);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("來源授權線索", doc.Body!.TextContent);
                Assert.Contains("規則邊界與授權尚未解析或確認", doc.Body.TextContent);
                Assert.Contains("<script>alert(1)</script>", doc.Body.TextContent);
                Assert.DoesNotContain("<script>alert(1)</script>", body);
            }
            var evidenceUrl = $"/api/v1/admin/intelligence/sources/{signature.Id:D}/revisions/{signatureRevision.Id:D}/signature-evidence?path=yara%2Fexample.yar";
            using (var response = await admin.GetAsync(evidenceUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
                var evidence = await response.Content.ReadFromJsonAsync<SignatureEvidenceView>(cancellationToken: token);
                Assert.IsNotNull(evidence); Assert.IsTrue(evidence.IntegrityValid); Assert.HasCount(2, evidence.Lines);
            }
            using (var response = await admin.GetAsync(evidenceUrl + "&line=3", token))
            {
                var evidence = await response.Content.ReadFromJsonAsync<SignatureEvidenceView>(cancellationToken: token);
                Assert.IsNotNull(evidence); Assert.AreEqual(3, evidence.ContextLine); Assert.IsNotNull(evidence.Context);
                Assert.IsTrue(evidence.Context.Any(x => x.Text.Contains("rule example", StringComparison.Ordinal)));
            }
            using (var anon = PingTestSite.Client(factory))
            using (var forbidden = await anon.GetAsync(evidenceUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, forbidden.StatusCode);
            var issuesUrl = $"/api/v1/admin/intelligence/sources/{signature.Id:D}/revisions/{signatureRevision.Id:D}/ioc-issues?path=iocs%2Fhash-iocs.txt";
            using (var response = await admin.GetAsync(issuesUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(token);
                Assert.Contains("invalid-hash", body);
                Assert.DoesNotContain("SECRET-IOC-CONTENT", body);
            }
            using (var issuePage = await admin.GetAsync($"/settings/intelligence?source={signature.Id:D}&rulesRevision={signatureRevision.Id:D}&iocPath=iocs%2Fhash-iocs.txt", token))
            {
                var body = await issuePage.Content.ReadAsStringAsync(token);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("雜湊格式無效", doc.Body!.TextContent);
                Assert.DoesNotContain("SECRET-IOC-CONTENT", body);
            }
            using (var anon = PingTestSite.Client(factory))
            using (var forbidden = await anon.GetAsync(issuesUrl, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, forbidden.StatusCode);
            async Task<Dictionary<string, string>> Form(string url, string name)
            {
                using var page = await admin.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == name);
                return form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            }
            var kevUrl = $"/settings/intelligence?source={kev.Id:D}";
            var activate = await Form(kevUrl, "intelligence-activation");
            activate["ActivationInput.RevisionId"] = revision.Id.ToString("D");
            activate["ActivationInput.ConfirmReduction"] = "false";
            using (var response = await admin.PostAsync(kevUrl, new FormUrlEncodedContent(activate), token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                Assert.Contains("版本已啟用", doc.Body!.TextContent);
                var next = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-activation");
                var nextFields = next.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                nextFields["ActivationInput.RevisionId"] = secondRevision.Id.ToString("D");
                using var nextResponse = await admin.PostAsync(kevUrl, new FormUrlEncodedContent(nextFields), token);
                var nextDocument = await new HtmlParser().ParseDocumentAsync(await nextResponse.Content.ReadAsStringAsync(token), token);
                Assert.Contains("版本已啟用", nextDocument.Body!.TextContent);
            }
            var revoke = await Form(kevUrl, "intelligence-revocation");
            revoke["RevocationInput.RevisionId"] = secondRevision.Id.ToString("D");
            revoke["RevocationInput.Confirm"] = "true";
            using (var response = await admin.PostAsync(kevUrl, new FormUrlEncodedContent(revoke), token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                Assert.Contains("版本已撤銷", doc.Body!.TextContent);
                Assert.Contains("目前版本不可使用", doc.Body.TextContent);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-activation");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["ActivationInput.RevisionId"] = revision.Id.ToString("D");
                using var restore = await admin.PostAsync(kevUrl, new FormUrlEncodedContent(fields), token);
                var restored = await new HtmlParser().ParseDocumentAsync(await restore.Content.ReadAsStringAsync(token), token);
                Assert.Contains("版本已啟用", restored.Body!.TextContent);
            }
            var ghUrl = $"/settings/intelligence?source={gh.Id:D}";
            using (var auditPage = await admin.GetAsync("/settings/audit?source=intelligence", token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await auditPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("撤銷情報版本", doc.Body!.TextContent);
                Assert.Contains(secondRevision.Id.ToString("D"), doc.Body.TextContent);
            }
            var credentials = await Form(ghUrl, "intelligence-credential");
            credentials["CredentialInput.Clear"] = "false";
            credentials["CredentialInput.Value"] = "sensitive-test-token";
            using (var response = await admin.PostAsync(ghUrl, new FormUrlEncodedContent(credentials), token))
            {
                var body = await response.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("sensitive-test-token", body);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("憑證設定已儲存", doc.Body!.TextContent);
                var clearForm = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-credential");
                var clear = clearForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                clear["CredentialInput.Clear"] = "True";
                using var cleared = await admin.PostAsync(ghUrl, new FormUrlEncodedContent(clear), token);
                var clearedDocument = await new HtmlParser().ParseDocumentAsync(await cleared.Content.ReadAsStringAsync(token), token);
                Assert.Contains("憑證設定已儲存", clearedDocument.Body!.TextContent);
            }
            using (var auditPage = await admin.GetAsync("/settings/audit?source=intelligence", token))
            {
                var body = await auditPage.Content.ReadAsStringAsync(token);
                var doc = await new HtmlParser().ParseDocumentAsync(body, token);
                Assert.Contains("更新來源憑證", doc.Body!.TextContent);
                Assert.Contains(gh.Id.ToString("D"), doc.Body.TextContent);
                Assert.DoesNotContain("sensitive-test-token", body);
            }
            await using var check = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(revision.Id, (await check.IntelligenceActivations.SingleAsync(x => x.SourceId == kev.Id, token)).RevisionId);
            Assert.IsTrue((await check.IntelligenceRevisions.SingleAsync(x => x.Id == secondRevision.Id, token)).Revoked);
            Assert.IsNull((await check.IntelligenceSources.SingleAsync(x => x.Id == gh.Id, token)).ProtectedCredential);
        }, context.CancellationToken);

    [TestMethod]
    public Task ManagementPage_ProtectsAccessAndSavesSourcePolicy() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    { ["ConnectionStrings:Vigiloom"] = database.ConnectionString }));
            });
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true };
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            { db.IntelligenceSources.Add(source); await db.SaveChangesAsync(token); }
            using var anonymous = PingTestSite.Client(factory);
            using var reader = PingTestSite.Client(factory);
            using var admin = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            await PingTestSite.LoginAsync(admin, "admin", token);
            var url = $"/settings/intelligence?source={source.Id:D}";
            using (var response = await anonymous.GetAsync(url, token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
            using (var response = await reader.GetAsync(url, token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
            using var page = await admin.GetAsync(url, token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
            Assert.Contains("CISA Known Exploited Vulnerabilities", document.Body!.TextContent);
            var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-policy");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            fields["PolicyInput.Enabled"] = "true";
            fields["PolicyInput.Interval"] = "43200";
            fields["PolicyInput.Stale"] = "259200";
            fields["PolicyInput.MiB"] = "16";
            fields["PolicyInput.Records"] = "50000";
            using var saved = await admin.PostAsync(url, new FormUrlEncodedContent(fields), token);
            Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            var savedDocument = await new HtmlParser().ParseDocumentAsync(await saved.Content.ReadAsStringAsync(token), token);
            Assert.Contains("設定已儲存", savedDocument.Body!.TextContent);
            var nextForm = savedDocument.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-policy");
            var nextFields = nextForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            nextFields["PolicyInput.Enabled"] = "true";
            nextFields["PolicyInput.Interval"] = "21600";
            nextFields["PolicyInput.Stale"] = "259200";
            nextFields["PolicyInput.MiB"] = "16";
            nextFields["PolicyInput.Records"] = "50000";
            using var savedAgain = await admin.PostAsync(url, new FormUrlEncodedContent(nextFields), token);
            var againDocument = await new HtmlParser().ParseDocumentAsync(await savedAgain.Content.ReadAsStringAsync(token), token);
            Assert.Contains("設定已儲存", againDocument.Body!.TextContent);
            var latestForm = againDocument.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-policy");
            var latestFields = latestForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            latestFields["PolicyInput.Enabled"] = "true";
            latestFields["PolicyInput.Interval"] = "43200";
            latestFields["PolicyInput.Stale"] = "259200";
            latestFields["PolicyInput.MiB"] = "16";
            latestFields["PolicyInput.Records"] = "50000";
            foreach (var (key, value) in new[] { ("PolicyInput.Enabled", "invalid"), ("PolicyInput.Interval", "99999999999999999999"), ("PolicyInput.SourceId", "not-a-guid"), ("PolicyInput.MiB", "0.0000001") })
            {
                var malformed = new Dictionary<string, string>(latestFields) { [key] = value };
                using var rejected = await admin.PostAsync(url, new FormUrlEncodedContent(malformed), token);
                Assert.AreNotEqual(HttpStatusCode.InternalServerError, rejected.StatusCode);
            }
            await using var check = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token);
            var persisted = await check.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
            Assert.AreEqual(21600, persisted.UpdateIntervalSeconds);
            Assert.IsTrue(persisted.Enabled);
            var exact = new Dictionary<string, string>(latestFields) { ["PolicyInput.MiB"] = "0.0009765625" };
            using var exactResponse = await admin.PostAsync(url, new FormUrlEncodedContent(exact), token);
            var exactDocument = await new HtmlParser().ParseDocumentAsync(await exactResponse.Content.ReadAsStringAsync(token), token);
            Assert.Contains("設定已儲存", exactDocument.Body!.TextContent);
            await check.Entry(persisted).ReloadAsync(token);
            Assert.AreEqual(1024, persisted.MaximumBytes);
            var toggleForm = exactDocument.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "intelligence-policy");
            var toggle = toggleForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
            toggle["PolicyInput.Enabled"] = "false"; toggle["PolicyInput.Interval"] = "43200"; toggle["PolicyInput.Stale"] = "259200";
            toggle["PolicyInput.MiB"] = "0.0009765625"; toggle["PolicyInput.Records"] = "50000";
            using var toggled = await admin.PostAsync(url, new FormUrlEncodedContent(toggle), token);
            Assert.Contains("設定已儲存", (await new HtmlParser().ParseDocumentAsync(await toggled.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            using var audit = await admin.GetAsync("/settings/audit?source=intelligence", token);
            var auditBody = (await new HtmlParser().ParseDocumentAsync(await audit.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("啟用：啟用 → 停用", auditBody);
            Assert.Contains(source.Id.ToString("D"), auditBody);
        }, context.CancellationToken);

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
