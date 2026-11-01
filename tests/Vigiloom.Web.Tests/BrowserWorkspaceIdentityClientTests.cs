using Vigiloom.Contracts.Workspace;
using Vigiloom.Web.Client.Components;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class BrowserWorkspaceIdentityClientTests
{
    [TestMethod]
    public async Task GetAsync_PropagatesCancellationToTheHttpRequest()
    {
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
        var type = typeof(TimeSeriesChart).Assembly.GetType("Vigiloom.Web.Client.Services.BrowserWorkspaceIdentityClient", throwOnError: true)!;
        var client = (IWorkspaceIdentityClient)Activator.CreateInstance(type, http)!;
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync(cancellation.Token);
        await handler.Started.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay cannot complete without cancellation.");
        }
    }
}
