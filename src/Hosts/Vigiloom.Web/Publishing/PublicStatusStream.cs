using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Publishing;

/// <summary>Streams only freshly reauthorized public resource projections.</summary>
internal sealed class PublicStatusStream(PublicationResourceKind kind, Guid id, PublicReader reader, PublicStreamAdmission admission) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        if (!await admission.Slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.RetryAfter = "15";
            await Results.Problem(statusCode: 503, title: "Public subscriptions are busy; please retry.").ExecuteAsync(context);
            return;
        }

        using var expiry = new CancellationTokenSource(admission.Lifetime, admission.Clock);
        var clientDisconnected = context.RequestAborted;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(clientDisconnected, expiry.Token);
        using var abortExpiredResponse = expiry.Token.Register(() =>
        {
            if (context.Response.HasStarted) context.Abort();
        });
        context.RequestAborted = lifetime.Token;
        try
        {
            var initial = await ReadBoundedAsync(lifetime.Token);
            if (initial is null) { await Results.NotFound().ExecuteAsync(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            await TypedResults.ServerSentEvents(Events(initial, lifetime.Token)).ExecuteAsync(context);
        }
        catch (OperationCanceledException) when (clientDisconnected.IsCancellationRequested) { }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested)
        {
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.RequestAborted = clientDisconnected;
                context.Response.Headers.RetryAfter = "15";
                await Results.Problem(statusCode: 503, title: "The public subscription expired; please retry.").ExecuteAsync(context);
            }
        }
        catch (TimeoutException)
        {
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Headers.RetryAfter = "15";
                await Results.Problem(statusCode: 503, title: "The public status request timed out; please retry.").ExecuteAsync(context);
            }
        }
        finally
        {
            context.RequestAborted = clientDisconnected;
            admission.Slots.Release();
        }
    }

    private async IAsyncEnumerable<SseItem<PublicStatusUpdate>> Events(PublicStatusUpdate initial,
        [EnumeratorCancellation] CancellationToken token)
    {
        yield return new(initial, "status");
        using var timer = new PeriodicTimer(admission.Interval, admission.Clock);
        while (await timer.WaitForNextTickAsync(token))
        {
            var current = await ReadBoundedAsync(token);
            if (current is null)
            {
                yield return new(new(null, null, null), "unavailable");
                yield break;
            }
            yield return new(current, "status");
        }
    }

    private async Task<PublicStatusUpdate?> ReadBoundedAsync(CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(admission.ReadTimeout, admission.Clock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await ReadAsync(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(); }
    }

    private async Task<PublicStatusUpdate?> ReadAsync(CancellationToken token)
    {
        switch (kind)
        {
            case PublicationResourceKind.Device:
                var device = await reader.DeviceAsync(id, token);
                return device is null ? null : new(device, null, null);
            case PublicationResourceKind.Service:
                var service = await reader.ServiceAsync(id, token);
                return service is null ? null : new(null, service, null);
            case PublicationResourceKind.Group:
                var group = await reader.GroupAsync(id, token);
                return group is null ? null : new(null, null, group);
            default:
                return null;
        }
    }
}
