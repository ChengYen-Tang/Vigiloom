using Microsoft.JSInterop;

namespace Vigiloom.Web.Client.Components.PublicStatus;

/// <summary>Owns one browser EventSource for the selected public resource.</summary>
internal sealed class PublicStatusSubscription(IJSRuntime javascript, PublicStatusPanel owner) : IAsyncDisposable
{
    private const string ModulePath = "./Components/PublicStatus/PublicStatusPanel.razor.js";
    private IJSObjectReference? module;
    private Task<IJSObjectReference>? moduleTask;
    private IJSObjectReference? instance;
    private Task<IJSObjectReference>? instanceTask;
    private DotNetObjectReference<PublicStatusPanel>? reference;

    public async Task StartAsync(string kind, Guid id, int generation, CancellationToken token)
    {
        try { module ??= await (moduleTask ??= javascript.InvokeAsync<IJSObjectReference>("import", token, ModulePath).AsTask()); }
        catch { moduleTask = null; throw; }
        if (!owner.IsSubscriptionCurrent(generation)) return;
        reference ??= DotNetObjectReference.Create(owner);
        try { instance ??= await (instanceTask ??= module.InvokeAsync<IJSObjectReference>("create", token, reference).AsTask()); }
        catch { instanceTask = null; throw; }
        if (!owner.IsSubscriptionCurrent(generation)) return;
        await instance.InvokeVoidAsync("start", token, kind, id.ToString("D"), generation);
    }

    public async Task StopAsync(int generation)
    {
        var created = instance ?? (instanceTask is null ? null : await instanceTask);
        if (created is not null) await created.InvokeVoidAsync("stop", generation);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var created = instance ?? (instanceTask is null ? null : await instanceTask);
            if (created is not null) { await created.InvokeVoidAsync("dispose"); await created.DisposeAsync(); }
            var loaded = module ?? (moduleTask is null ? null : await moduleTask);
            if (loaded is not null) await loaded.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (OperationCanceledException) { }
        reference?.Dispose();
    }
}
