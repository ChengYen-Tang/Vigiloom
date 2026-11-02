using Microsoft.Playwright;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Ensures an Auto component first exercises its Server path before caching WASM.</summary>
internal sealed class AutoRenderGate(IPage page) : IAsyncDisposable
{
    private const string Pattern = "**/_framework/*.wasm*";
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool released;

    public static async Task<AutoRenderGate> HoldAsync(IPage page)
    {
        var gate = new AutoRenderGate(page);
        await page.RouteAsync(Pattern, async route => { await gate.release.Task; await route.ContinueAsync(); });
        return gate;
    }

    public async Task ReleaseAsync()
    {
        if (released) return;
        released = true;
        release.TrySetResult();
        await page.UnrouteAsync(Pattern);
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
