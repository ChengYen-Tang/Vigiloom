using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Temporarily rejects real Agent batches so the test can observe a durable retry.</summary>
internal sealed class AgentBatchGate : IStartupFilter
{
    private readonly TaskCompletionSource<byte[]> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]> retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool reject = true;

    internal Task<byte[]> Blocked => blocked.Task;
    internal Task<byte[]> Retried => retried.Task;
    internal void Allow() => reject = false;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (context.Request.Method != "POST" || context.Request.Path != "/api/v1/agent/batches")
            {
                await following(context);
                return;
            }

            using var wire = new MemoryStream();
            await context.Request.Body.CopyToAsync(wire, context.RequestAborted);
            wire.Position = 0;
            using var decoded = new MemoryStream();
            if (context.Request.Headers.ContentEncoding.ToString() == "gzip")
            {
                using var gzip = new GZipStream(wire, CompressionMode.Decompress, leaveOpen: true);
                await gzip.CopyToAsync(decoded, context.RequestAborted);
            }
            else await wire.CopyToAsync(decoded, context.RequestAborted);
            var payload = decoded.ToArray();
            if (AgentMetricBatchCodec.TryRead(payload, out _))
            {
                if (reject)
                {
                    blocked.TrySetResult(payload);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                if (blocked.Task.IsCompletedSuccessfully && payload.AsSpan().SequenceEqual(blocked.Task.Result))
                    retried.TrySetResult(payload);
            }
            wire.Position = 0;
            context.Request.Body = wire;
            await following(context);
        });
        next(app);
    };
}
