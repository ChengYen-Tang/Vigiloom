using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class IntelligenceBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task SourceSettings_WorkAtDesktopAndMobileWidths() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        var source = new IntelligenceSource { Key = "cisa-kev", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow,
            LastError = "KEV retrieval failed." };
        var advisorySource = new IntelligenceSource { Key = "github-reviewed", Enabled = true };
        var sigmaSource = new IntelligenceSource { Key = "sigmahq-rules", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
        var signatureSource = new IntelligenceSource { Key = "signature-base", Enabled = true, LastSuccessfulCheckAt = DateTimeOffset.UtcNow };
        var revision = new IntelligenceRevision { SourceId = source.Id, UpstreamVersion = "test-catalog-v1", ReleasedAt = DateTimeOffset.UtcNow.AddDays(-1),
            RetrievedAt = DateTimeOffset.UtcNow, Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test",
            LicenseEvidence = "test", RecordCount = 1, Normalized = true };
        var sigmaRevision = new IntelligenceRevision { SourceId = sigmaSource.Id, UpstreamVersion = "sigma-test", RetrievedAt = DateTimeOffset.UtcNow,
            Digest = new byte[32], Artifact = [], SchemaVersion = "sigma-yaml-source-v1", MappingVersion = "sigma-rule-source-v2",
            LicenseEvidence = "SigmaHQ rules: DRL-1.1, subject to individual rule terms; SigmaHQ/sigma@54967d64e6dda2b4daa81f47f7a8bf1f591b68a0", RecordCount = 1, Normalized = true };
        var signatureRevision = new IntelligenceRevision { SourceId = signatureSource.Id, UpstreamVersion = "signature-test", RetrievedAt = DateTimeOffset.UtcNow,
            Digest = new byte[32], Artifact = [], SchemaVersion = "signature-base-files-v1", MappingVersion = "signature-base-source-v2",
            LicenseEvidence = "Signature-Base repository terms and per-rule exceptions require review; Neo23x0/signature-base@eb8c53cb08ed103d031fe9c98d1d23e94904839d", RecordCount = 2, Normalized = true };
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
        {
            await db.Database.MigrateAsync(token);
            db.IntelligenceSources.AddRange(source, advisorySource, sigmaSource, signatureSource);
            db.IntelligenceRevisions.AddRange(revision, sigmaRevision, signatureRevision);
            db.SigmaRules.Add(new SigmaRule { RevisionId = sigmaRevision.Id, Path = "rules/windows/example.yml", Title = "Example rule",
                Author = "Example author", Product = "windows", Digest = new byte[32], Content = [1] });
            var file = new SignatureFile { RevisionId = signatureRevision.Id, Path = "iocs/hash-iocs.txt", Kind = 0, Digest = new byte[32], Content = [1] };
            db.SignatureFiles.Add(file);
            var yara = Encoding.UTF8.GetBytes("rule sample {\n meta:\n author = \"Example author\"\n condition: true\n}");
            db.SignatureFiles.Add(new SignatureFile { RevisionId = signatureRevision.Id, Path = "yara/example.yar", Kind = 1, Digest = SHA256.HashData(yara), Content = yara });
            db.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
            db.IocEntries.Add(new IocEntry { SignatureFileRowId = file.RowId, LineNumber = 5, Kind = "unsupported", Reason = "invalid-hash" });
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("VIGILOOM_BROWSER_EXECUTABLE") });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(url + "/settings/intelligence");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "情報來源", Exact = true }).First.WaitForAsync();
        await page.GetByText("更新設定", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("更新間隔（秒）").FillAsync("43200");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        Assert.Contains("設定已儲存", await page.Locator("main").InnerTextAsync());
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_INTELLIGENCE_EVIDENCE_DIR") ?? Path.Combine(context.TestRunDirectory!, "intelligence-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(url + "/settings/intelligence");
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var versions = page.GetByRole(AriaRole.Region, new() { Name = "情報版本，可左右捲動" });
        await versions.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('[aria-label=\"情報版本，可左右捲動\"]')?.scrollLeft > 0");
        await page.GetByText("更新設定", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("啟用自動更新").UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        Assert.Contains("設定已儲存", await page.Locator("main").InnerTextAsync());
        await using (var disabledCheck = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            Assert.IsFalse((await disabledCheck.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token)).Enabled);
        await page.GetByText("更新設定", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("啟用自動更新").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        Assert.Contains("設定已儲存", await page.Locator("main").InnerTextAsync());
        await page.GetByText("更新設定", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("匯入容量上限（MiB）").FillAsync("1.5");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        Assert.Contains("設定已儲存", await page.Locator("main").InnerTextAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        await page.Locator(".intelligence-source").Filter(new() { HasText = "SigmaHQ 規則" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "規則來源資料" }).WaitForAsync();
        Assert.Contains("Example rule", await page.Locator("main").InnerTextAsync());
        Assert.Contains("不會部署規則或啟用偵測", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "sigma-mobile.png"), FullPage = true });
        await page.Locator(".intelligence-source").Filter(new() { HasText = "Signature-Base" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "IOC／YARA 來源檔案" }).WaitForAsync();
        Assert.Contains("iocs/hash-iocs.txt", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Link, new() { Name = "1 筆未支援" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "未支援的 IOC 項目" }).WaitForAsync();
        Assert.Contains("雜湊格式無效", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Link, new() { Name = "查看授權線索" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "來源授權線索" }).WaitForAsync();
        Assert.Contains("Example author", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByRole(AriaRole.Link, new() { Name = "查看前後內容" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "第 3 行的前後內容" }).WaitForAsync();
        Assert.Contains("rule sample", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "signature-mobile.png"), FullPage = true });
        Assert.IsEmpty(errors);
        await using var check = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token);
        var current = await check.IntelligenceSources.SingleAsync(x => x.Id == source.Id, token);
        Assert.IsTrue(current.Enabled);
        Assert.AreEqual(43200, current.UpdateIntervalSeconds);
        Assert.AreEqual(1572864, current.MaximumBytes);
    }, context.CancellationToken);
}
