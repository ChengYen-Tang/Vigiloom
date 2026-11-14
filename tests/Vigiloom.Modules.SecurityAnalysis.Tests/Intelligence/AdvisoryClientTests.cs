using System.Net;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class AdvisoryClientTests
{
    private const string Advisory = """
        {"ghsa_id":"GHSA-p82g-2xpp-m5r3","cve_id":"CVE-2015-5654","type":"reviewed",
        "published_at":"2020-09-11T21:18:05Z","updated_at":"2023-01-06T05:00:00Z","withdrawn_at":null,
        "vulnerabilities":[{"package":{"ecosystem":"npm","name":"dojo"},"vulnerable_version_range":"< 1.2.0","first_patched_version":"1.2.0"}]}
        """;
    private const string Next = "<https://api.github.com/advisories?type=reviewed&per_page=100&sort=updated&direction=asc&modified=2023-01-01..2023-02-01&after=Y3Vyc29y%3D>; rel=\"next\"";

    [TestMethod]
    public async Task FetchAsync_ValidPage_PreservesRangeAndNextCursor()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            Assert.AreEqual("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"[{Advisory}]", Encoding.UTF8, "application/json") };
            response.Headers.TryAddWithoutValidation("Link", Next);
            return response;
        }));
        var page = await new AdvisoryClient(client, TimeProvider.System).FetchAsync(new(2023, 1, 1), new(2023, 2, 1), false, null, null, CancellationToken.None);
        Assert.AreEqual("Y3Vyc29y=", page.NextCursor);
        Assert.HasCount(1, page.Records);
        Assert.AreEqual("< 1.2.0", page.Records[0].Packages[0].VulnerableRange);
        Assert.Contains("CVE-2015-5654", page.Records[0].OriginalJson);
    }

    [TestMethod]
    public async Task FetchAsync_RejectsChangedQueryAndRateLimitIsExplicit()
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"[{Advisory}]") };
            response.Headers.TryAddWithoutValidation("Link", Next.Replace("type=reviewed", "type=malware", StringComparison.Ordinal));
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdvisoryClient(client, TimeProvider.System)
            .FetchAsync(new(2023, 1, 1), new(2023, 2, 1), false, null, null, CancellationToken.None));
        using var limited = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
            return response;
        }));
        var error = await Assert.ThrowsAsync<AdvisoryRateLimitException>(() => new AdvisoryClient(limited, TimeProvider.System)
            .FetchAsync(new(2023, 1, 1), new(2023, 2, 1), false, null, null, CancellationToken.None));
        Assert.IsNotNull(error.RetryAt);
        using var secondary = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                { Content = new StringContent("{\"message\":\"You have exceeded a secondary rate limit.\"}") };
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "100");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds().ToString());
            return response;
        }));
        var before = DateTimeOffset.UtcNow;
        var secondaryError = await Assert.ThrowsAsync<AdvisoryRateLimitException>(() => new AdvisoryClient(secondary, TimeProvider.System)
            .FetchAsync(new(2023, 1, 1), new(2023, 2, 1), false, null, null, CancellationToken.None));
        Assert.IsNotNull(secondaryError.RetryAt);
        Assert.IsGreaterThanOrEqualTo(before.AddMinutes(1), secondaryError.RetryAt.Value);
    }

    [TestMethod]
    public void Validator_RejectsDuplicateIdsUnreviewedAndInvalidUtf8()
    {
        var duplicate = Encoding.UTF8.GetBytes($"[{Advisory},{Advisory}]");
        var unreviewed = Encoding.UTF8.GetBytes($"[{Advisory.Replace("reviewed", "unreviewed", StringComparison.Ordinal)}]");
        var invalid = Encoding.UTF8.GetBytes($"[{Advisory}]");
        invalid[10] = 0xff;
        Assert.Throws<InvalidDataException>(() => AdvisoryPageValidator.Validate(duplicate));
        Assert.Throws<InvalidDataException>(() => AdvisoryPageValidator.Validate(unreviewed));
        Assert.Throws<InvalidDataException>(() => AdvisoryPageValidator.Validate(invalid));
        var localTime = Encoding.UTF8.GetBytes($"[{Advisory.Replace("2020-09-11T21:18:05Z", "2020-09-11T21:18:05", StringComparison.Ordinal)}]");
        Assert.Throws<InvalidDataException>(() => AdvisoryPageValidator.Validate(localTime));
    }

    [TestMethod]
    public async Task FetchAsync_WithdrawnFilterRejectsActiveRecord()
    {
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"[{Advisory}]") }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AdvisoryClient(client, TimeProvider.System)
            .FetchAsync(new(2023, 1, 1), new(2023, 2, 1), true, null, null, CancellationToken.None));
    }

    [TestMethod]
    public void Pagination_RejectsMalformedRelationsAndAcceptsOtherValidRelations()
    {
        const string window = "2023-01-01..2023-02-01";
        Assert.Throws<InvalidDataException>(() => AdvisoryLinks.Next([Next.Replace("rel=\"next\"", "rel=\"next\t\"", StringComparison.Ordinal)], window, false));
        Assert.Throws<InvalidDataException>(() => AdvisoryLinks.Next([Next.Replace("rel=\"next\"", "rel=\"next", StringComparison.Ordinal)], window, false));
        Assert.IsNull(AdvisoryLinks.Next([Next.Replace("rel=\"next\"", "rel=\"prev\"", StringComparison.Ordinal)], window, false));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
}
