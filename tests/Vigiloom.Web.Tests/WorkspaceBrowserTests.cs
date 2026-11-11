using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class WorkspaceBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AutoWorkspace_UsesServerThenCachedWebAssembly_AndRechecksAuthorization() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new BrowserFactory(database.ConnectionString, setupSecret);
            factory.UseKestrel(0);
            factory.StartServer();
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                    .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = new Account { UserName = "browser-reader" };
                Assert.IsTrue((await users.CreateAsync(reader, "Reader-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
            }
            var baseAddress = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await using var wasmGate = await AutoRenderGate.HoldAsync(page);
            var circuit = new TaskCompletionSource<IWebSocketRoute>(TaskCreationOptions.RunContinuationsAsynchronously);
            await page.RouteWebSocketAsync("**/_blazor*", socket =>
            {
                socket.ConnectToServer();
                circuit.TrySetResult(socket);
            });
            var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            page.Console += (_, message) => diagnostics.Enqueue($"console {message.Type}: {message.Text}");
            page.PageError += (_, message) => diagnostics.Enqueue($"page error: {message}");
            page.Response += (_, response) => { if (response.Status >= 400 && response.Url.Contains("/_framework/", StringComparison.Ordinal)) diagnostics.Enqueue($"framework response: {response.Status} {new Uri(response.Url).AbsolutePath}"); };
            page.RequestFailed += (_, request) => { if (request.Url.Contains("/_framework/", StringComparison.Ordinal)) diagnostics.Enqueue($"framework request: {request.Failure} {new Uri(request.Url).AbsolutePath}"); };
            await page.GotoAsync(baseAddress + "/workspace");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("browser-reader");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Reader-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.Locator("[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
            await wasmGate.ReleaseAsync();
            Assert.AreEqual("browser-reader", await page.Locator("#workspace-user").InnerTextAsync());
            Assert.Contains("Read only", await page.Locator("main.workspace").InnerTextAsync());
            await page.WaitForFunctionAsync("() => performance.getEntriesByType('resource').some(x => x.name.includes('/_framework/Vigiloom.Web.Client.') && x.name.includes('.wasm') && x.responseEnd > 0)",
                null, new() { Timeout = 90000 });
            await context.SetOfflineAsync(true);
            await (await circuit.Task.WaitAsync(TimeSpan.FromSeconds(30), token)).CloseAsync(new() { Code = 1001, Reason = "Simulated network interruption" });
            await page.Locator("#components-reconnect-modal[open]").WaitForAsync(new() { Timeout = 30000 });
            Assert.Contains("連線", await page.Locator("#components-reconnect-modal").InnerTextAsync());
            await context.SetOfflineAsync(false);
            await page.Locator("#components-reconnect-modal[open]").WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 60000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "重新確認登入狀態" }).ClickAsync();
            await page.Locator("#workspace-user").WaitForAsync();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = await users.FindByNameAsync("browser-reader");
                Assert.IsNotNull(reader);
                Assert.IsTrue((await users.RemoveFromRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.Administrator)).Succeeded);
                Assert.IsTrue((await users.UpdateSecurityStampAsync(reader)).Succeeded);
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "重新確認登入狀態" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "登入已過期或權限已變更" }).WaitForAsync();
            Assert.AreEqual(0, await page.Locator("#workspace-user").CountAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "前往登入" }).ClickAsync();
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("browser-reader");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Reader-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.Locator("[data-runtime='Server'], [data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
            await page.Locator("#workspace-user").WaitForAsync(new() { Timeout = 60000 });
            Assert.Contains("Administrator", await page.Locator("main.workspace").InnerTextAsync());
            var initialResources = await page.EvaluateAsync<string>("""
                async () => JSON.stringify({ caches: await caches.keys(), resources: performance.getEntriesByType('resource')
                    .filter(entry => entry.name.includes('/_framework/')).map(entry => new URL(entry.name).pathname).slice(-12) })
                """);

            // Keep the same browser context so Auto can reuse its downloaded WebAssembly assets.
            var reachedWebAssembly = await page.Locator("[data-runtime='WebAssembly']").CountAsync() > 0;
            for (var attempt = 0; attempt < 2 && !reachedWebAssembly; attempt++)
            {
                await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
                await page.ReloadAsync();
                try
                {
                    await page.Locator("[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 5000 });
                    reachedWebAssembly = true;
                }
                catch (TimeoutException) { }
            }
            var finalResources = await page.EvaluateAsync<string>("""
                async () => JSON.stringify({ caches: await caches.keys(), resources: performance.getEntriesByType('resource')
                    .filter(entry => entry.name.includes('/_framework/')).map(entry => new URL(entry.name).pathname).slice(-12) })
                """);
            Assert.IsTrue(reachedWebAssembly, $"Auto did not switch to WebAssembly on a subsequent visit. Initial {initialResources}; final {finalResources}; diagnostics {string.Join(" | ", diagnostics.TakeLast(15))}");
            Assert.AreEqual("browser-reader", await page.Locator("#workspace-user").InnerTextAsync());
            await context.SetOfflineAsync(true);
            await page.GetByRole(AriaRole.Button, new() { Name = "重新確認登入狀態" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "暫時無法確認登入狀態" }).WaitForAsync();
            Assert.AreEqual(0, await page.Locator("#workspace-user").CountAsync());
            await context.SetOfflineAsync(false);
            await page.GetByRole(AriaRole.Button, new() { Name = "重新嘗試" }).ClickAsync();
            await page.Locator("#workspace-user").WaitForAsync();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = await users.FindByNameAsync("browser-reader");
                Assert.IsNotNull(reader);
                Assert.IsTrue((await users.UpdateSecurityStampAsync(reader)).Succeeded);
            }
            await page.GetByRole(AriaRole.Button, new() { Name = "重新確認登入狀態" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "登入已過期或權限已變更" }).WaitForAsync();
            Assert.AreEqual(0, await page.Locator("#workspace-user").CountAsync());
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServerCircuit_RejectsAnExpiredCookieWithoutAStampChange() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new BrowserFactory(database.ConnectionString, setupSecret, TimeSpan.FromSeconds(15));
            factory.UseKestrel(0);
            factory.StartServer();
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            await using (var scope = factory.Services.CreateAsyncScope())
                Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                    .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
            var baseAddress = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            await using var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();
            await using var wasmGate = await AutoRenderGate.HoldAsync(page);
            await page.GotoAsync(baseAddress + "/workspace");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            var beforeLogin = DateTimeOffset.UtcNow;
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.Locator("[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
            await wasmGate.ReleaseAsync();
            Assert.AreEqual("admin", await page.Locator("#workspace-user").InnerTextAsync());
            var remaining = beforeLogin.AddSeconds(17) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
            await page.GetByRole(AriaRole.Button, new() { Name = "重新確認登入狀態" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "登入已過期或權限已變更" }).WaitForAsync();
            Assert.AreEqual(0, await page.Locator("#workspace-user").CountAsync());
        }, TestContext.CancellationToken);

    [TestMethod]
    public async Task TimeSeriesChart_RendersRealTimeAxisInServerAndWebAssembly()
    {
        using var factory = new BrowserFactory("Server=unused;Database=unused", "unused");
        factory.UseKestrel(0);
        factory.StartServer();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync(factory.ClientOptions.BaseAddress.ToString().TrimEnd('/') + "/__test/chart");
        await AssertChartAsync(page, "Server");
        Assert.IsNull(await page.EvaluateAsync<string?>("() => localStorage.getItem('blazor-resource-hash:Vigiloom.Web.Client')"));
        await wasmGate.ReleaseAsync();
        await page.WaitForFunctionAsync("() => performance.getEntriesByType('resource').some(x => x.name.includes('/_framework/Vigiloom.Web.Client.') && x.name.includes('.wasm') && x.responseEnd > 0)", null, new() { Timeout = 90000 });
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await AssertChartAsync(page, "WebAssembly");
    }

    private static async Task AssertChartAsync(IPage page, string runtime)
    {
        await page.Locator($"figure[data-runtime='{runtime}']").WaitForAsync(new() { Timeout = 60000 });
        await page.WaitForFunctionAsync("() => { const el = document.querySelector('.time-series-chart [role=img] > div'); return el && window.echarts?.getInstanceByDom(el); }", null, new() { Timeout = 60000 });
        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>("""
            () => {
                const chart = window.echarts.getInstanceByDom(document.querySelector('.time-series-chart [role=img] > div'));
                const options = chart.getOption();
                return { axis: options.xAxis[0].type, data: options.series[0].data, connect: options.series[0].connectNulls };
            }
            """);
        Assert.AreEqual("time", result.GetProperty("axis").GetString());
        Assert.IsFalse(result.GetProperty("connect").GetBoolean());
        var data = result.GetProperty("data");
        Assert.AreEqual(3, data.GetArrayLength());
        Assert.AreEqual(60000L, data[1][0].GetInt64() - data[0][0].GetInt64());
        Assert.AreEqual(43140000L, data[2][0].GetInt64() - data[1][0].GetInt64());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, data[1][1].ValueKind);
    }

    private sealed class BrowserFactory(string connectionString, string setupSecret, TimeSpan? cookieLifetime = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = connectionString,
                ["Vigiloom:SetupToken"] = setupSecret,
                ["Vigiloom:Authentication:RequestsPerMinute"] = "100",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Warning"
            }));
            builder.ConfigureServices(services => services.AddTransient<IStartupFilter, ChartHarnessStartupFilter>());
            if (cookieLifetime is { } lifetime)
                builder.ConfigureServices(services => services.ConfigureApplicationCookie(options =>
                {
                    options.ExpireTimeSpan = lifetime;
                    options.SlidingExpiration = false;
                }));
        }
    }
}
