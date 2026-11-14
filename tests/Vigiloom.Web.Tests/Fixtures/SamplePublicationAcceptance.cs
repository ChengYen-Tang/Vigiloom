using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Playwright;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Services;
using Vigiloom.Web.Api;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Checks that the real onboarding sample's retained evidence stays behind authorization.</summary>
internal static class SamplePublicationAcceptance
{
    internal static async Task VerifyAsync(Uri tlsAddress, Uri publicAddress, X509Certificate2 certificate,
        Guid serviceId, Guid sourceId, string sourceSecret, int samplePort, Func<CancellationToken, Task<ServiceHealthStatus?>> readHealth,
        Func<CancellationToken, Task> recheckHealth, CancellationToken token)
    {
        var origin = new UriBuilder(Uri.UriSchemeHttps, "localhost", tlsAddress.Port).Uri;
        var certificateHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        HttpClient Client() => new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented?.GetCertHashString(HashAlgorithmName.SHA256) == certificateHash
        }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(20) };

        using var admin = Client();
        using var reader = Client();
        using var anonymous = Client();
        await PingTestSite.LoginAsync(admin, "admin", token);
        await PingTestSite.LoginAsync(reader, "reader", token);
        using (var forbiddenSource = await reader.PostAsync($"/api/v1/service-discovery/sources/{sourceId:D}/revoke", null, token))
            Assert.AreEqual(HttpStatusCode.Forbidden, forbiddenSource.StatusCode);
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        var query = $"?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        var evidenceRoot = $"/api/v1/service-details/{serviceId:D}";
        var logs = await reader.GetFromJsonAsync<ServiceEvidencePage<ServiceLogEvidence>>($"{evidenceRoot}/logs{query}", token);
        var traces = await reader.GetFromJsonAsync<ServiceEvidencePage<ServiceTraceEvidence>>($"{evidenceRoot}/traces{query}", token);
        Assert.IsNotNull(logs); Assert.IsNotNull(traces);
        Assert.IsNotEmpty(logs.Items); Assert.IsNotEmpty(traces.Items);
        var logBody = logs.Items.Select(x => x.Body).FirstOrDefault(x => x.Contains("The sample received a request", StringComparison.Ordinal));
        Assert.IsNotNull(logBody, "A real log body is needed for the privacy assertion.");
        var traceId = traces.Items[0].TraceId;
        var privateValues = new[] { logBody, traceId, sourceSecret, $"http://127.0.0.1:{samplePort}/health" };
        void AssertPublic(string output)
        {
            foreach (var value in privateValues)
                Assert.IsFalse(output.Contains(value, StringComparison.Ordinal), "A private sample value appeared in public output.");
        }

        foreach (var path in new[] { $"{evidenceRoot}/logs{query}", $"{evidenceRoot}/traces{query}",
            $"/api/v1/service-telemetry/{serviceId:D}/logs{query}", $"/api/v1/service-telemetry/{serviceId:D}/traces{query}" })
        {
            using var denied = await anonymous.GetAsync(path, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var settingsPath = $"/api/v1/admin/publications/services/{serviceId:D}";
        var settings = await admin.GetFromJsonAsync<PublicationSettings>(settingsPath, token);
        Assert.IsNotNull(settings);
        var csrf = await admin.GetFromJsonAsync<AntiforgeryResponse>("/api/v1/session/antiforgery", token);
        Assert.IsNotNull(csrf);
        async Task<PublicationSettings> SetPublicAsync(bool publish, string version)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, settingsPath)
            { Content = JsonContent.Create(new PublicationChange(publish, publish ? "Public sample" : null,
                publish ? PublicationPolicy.ServiceHistoryDetailsVersion : null, version)) };
            request.Headers.Add("RequestVerificationToken", csrf.RequestToken);
            using var response = await admin.SendAsync(request, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<PublicationSettings>(token))!;
        }
        using (var forbidden = await reader.PutAsJsonAsync(settingsPath, new PublicationChange(true, "Public sample", PublicationPolicy.BaselineVersion, settings.Version), token))
            Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        settings = await SetPublicAsync(true, settings.Version);

        var invalidated = await readHealth(token);
        Assert.IsNotNull(invalidated);
        Assert.AreEqual(ServiceHealthState.Unknown, invalidated.State);
        Assert.IsTrue(invalidated.UnknownReasons.HasFlag(ServiceHealthUnknownReason.Invalidated));
        var waitingPublic = await anonymous.GetFromJsonAsync<PublicService>($"/api/v1/public/services/{serviceId:D}", token);
        Assert.IsNotNull(waitingPublic);
        Assert.AreEqual("Unknown", waitingPublic.State);
        await recheckHealth(token);

        foreach (var path in new[] { "/api/v1/public/services", $"/api/v1/public/services/{serviceId:D}", $"/api/v1/public/services/{serviceId:D}/history{query}" })
        {
            using var response = await anonymous.GetAsync(path, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            AssertPublic(await response.Content.ReadAsStringAsync(token));
        }
        var publicService = await anonymous.GetFromJsonAsync<PublicService>($"/api/v1/public/services/{serviceId:D}", token);
        Assert.IsNotNull(publicService);
        Assert.AreEqual(serviceId, publicService.Id);
        Assert.AreEqual("Healthy", publicService.State);
        var publicHistory = await anonymous.GetFromJsonAsync<PublicServiceHistory>($"/api/v1/public/services/{serviceId:D}/history{query}", token);
        Assert.IsNotNull(publicHistory);
        Assert.IsNotEmpty(publicHistory.Days);

        using var streamResponse = await anonymous.GetAsync($"/api/v1/public/services/{serviceId:D}/events", HttpCompletionOption.ResponseHeadersRead, token);
        Assert.AreEqual(HttpStatusCode.OK, streamResponse.StatusCode);
        using var stream = new StreamReader(await streamResponse.Content.ReadAsStreamAsync(token));
        async Task<string> ReadEventAsync(string type)
        {
            using var eventDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            eventDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            var text = new System.Text.StringBuilder();
            while (await stream.ReadLineAsync(eventDeadline.Token) is { } line)
            {
                if (line.Length == 0 && text.Length > 0)
                {
                    var message = text.ToString();
                    if (message.Contains($"event: {type}", StringComparison.Ordinal)) return message;
                    text.Clear();
                }
                else if (line.Length > 0) text.AppendLine(line);
            }
            throw new InvalidOperationException($"Stream ended before {type}.");
        }
        var initialEvent = await ReadEventAsync("status");
        Assert.Contains("Public sample", initialEvent);
        Assert.Contains("Healthy", initialEvent);
        AssertPublic(initialEvent);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        var bodies = new ConcurrentBag<Task<string>>();
        var privateRequests = new ConcurrentBag<string>();
        var listFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var historyFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserStreamOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureNetwork = false;
        page.Response += (_, response) =>
        {
            if (new Uri(response.Url).AbsolutePath.EndsWith("/events", StringComparison.Ordinal) && response.Status == 200)
                browserStreamOpened.TrySetResult();
        };
        page.Request += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (path.StartsWith("/api/", StringComparison.Ordinal) && !path.StartsWith("/api/v1/public/", StringComparison.Ordinal))
                privateRequests.Add(path);
        };
        page.RequestFinished += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (captureNetwork && path.StartsWith("/api/v1/public/", StringComparison.Ordinal) && !path.EndsWith("/events", StringComparison.Ordinal))
            {
                bodies.Add(ReadBodyAsync(request));
                if (path == "/api/v1/public/services") listFinished.TrySetResult();
                if (path == $"/api/v1/public/services/{serviceId:D}/history") historyFinished.TrySetResult();
            }
        };
        await using var render = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync(publicAddress.ToString());
        await page.Locator(".public-status[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Public sample", Exact = true }).WaitForAsync();
        AssertPublic(await page.ContentAsync());
        await render.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".public-status[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Public sample", Exact = true }).WaitForAsync();
        await page.Locator(".history-strip").WaitForAsync(new() { Timeout = 30000 });
        AssertPublic(await page.ContentAsync());
        captureNetwork = true;
        await page.GetByLabel("搜尋公開名稱").FillAsync("Public sample");
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Public sample", Exact = true }).WaitForAsync();
        await Task.WhenAll(listFinished.Task, historyFinished.Task).WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.IsEmpty(privateRequests);
        var captured = await Task.WhenAll(bodies.ToArray()).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.IsNotEmpty(captured);
        foreach (var body in captured) AssertPublic(body);

        await page.GetByRole(AriaRole.Button, new() { Name = "Public sample", Exact = true }).ClickAsync();
        await browserStreamOpened.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        settings = await SetPublicAsync(false, settings.Version);
        AssertPublic(await ReadEventAsync("unavailable"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Public sample", Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10000 });
        Assert.IsEmpty(privateRequests);
        using (var removed = await anonymous.GetAsync($"/api/v1/public/services/{serviceId:D}", token)) Assert.AreEqual(HttpStatusCode.NotFound, removed.StatusCode);
        using (var list = await anonymous.GetAsync("/api/v1/public/services", token))
            Assert.DoesNotContain("Public sample", await list.Content.ReadAsStringAsync(token));
        var stillReadable = await reader.GetFromJsonAsync<ServiceEvidencePage<ServiceTraceEvidence>>($"{evidenceRoot}/traces{query}", token);
        Assert.IsNotNull(stillReadable); Assert.IsNotEmpty(stillReadable.Items);
    }

    private static async Task<string> ReadBodyAsync(IRequest request)
    {
        var response = await request.ResponseAsync();
        Assert.IsNotNull(response);
        Assert.AreEqual(200, response.Status);
        return await response.TextAsync();
    }
}
