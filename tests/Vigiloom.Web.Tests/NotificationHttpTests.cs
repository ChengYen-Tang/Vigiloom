using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NotificationHttpTests(TestContext context)
{
    [TestMethod]
    public Task NotificationPage_RequiresLoginAndFiltersRealRecords() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            {
                db.Set<Notification>().AddRange(
                    new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", OccurredAt = now,
                        Title = "Ping 無法連線", Detail = "<script>alert(1)</script>", ResourceId = device.Id },
                    new Notification { MessageId = Guid.CreateVersion7(), Source = "sigma", OccurredAt = now,
                        Title = "行為規則命中", Detail = "需要調查", ResourceId = device.Id });
                await db.SaveChangesAsync(token);
            }
            using var anonymous = PingTestSite.Client(factory);
            using var denied = await anonymous.GetAsync("/notifications", token);
            Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using var page = await reader.GetAsync("/notifications?source=ping", token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(token);
            var document = await new HtmlParser().ParseDocumentAsync(html, token);
            Assert.Contains("Ping 無法連線", document.Body!.TextContent);
            Assert.DoesNotContain("行為規則命中", document.Body.TextContent);
            Assert.Contains("通知派送尚未接入", document.Body.TextContent);
            Assert.IsNull(document.QuerySelector(".notifications-table script"));
            Assert.IsNotNull(document.QuerySelector($"a[href='/devices/{device.Id:D}']"));
            using var invalid = await reader.GetAsync("/notifications?from=2026-11-03&to=2026-11-01", token);
            Assert.Contains("開始日期不能晚於結束日期", (await new HtmlParser().ParseDocumentAsync(await invalid.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            using var empty = await reader.GetAsync("/notifications?source=vulnerability", token);
            Assert.Contains("沒有符合條件的通知紀錄", (await new HtmlParser().ParseDocumentAsync(await empty.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
        }, context.CancellationToken);
}
