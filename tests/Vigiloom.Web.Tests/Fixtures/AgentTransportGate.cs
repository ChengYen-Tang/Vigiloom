using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Controls temporary transport failures during a real Agent update test.</summary>
internal sealed class AgentTransportGate : IStartupFilter
{
    private readonly TaskCompletionSource<byte[]> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<byte[]> retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource assignmentPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource assignmentReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource packagePaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource packageReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Func<CancellationToken, Task<bool>>? pauseAssignment;
    private int settingsRejections;
    private volatile bool reject = true;
    private volatile bool rejectSettings;
    private volatile bool pausePackage;

    internal Task<byte[]> Blocked => blocked.Task;
    internal Task<byte[]> Retried => retried.Task;
    internal int SettingsRejections => Volatile.Read(ref settingsRejections);
    internal Task AssignmentPaused => assignmentPaused.Task;
    internal Task PackagePaused => packagePaused.Task;
    internal void Allow() => reject = false;
    internal void BlockSettings() => rejectSettings = true;
    internal void PauseAssignmentWhen(Func<CancellationToken, Task<bool>> condition) => pauseAssignment = condition;
    internal void ReleaseAssignment() { pauseAssignment = null; assignmentReleased.TrySetResult(); }
    internal void PausePackage() => pausePackage = true;
    internal void ReleasePackage() { pausePackage = false; packageReleased.TrySetResult(); }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, following) =>
        {
            if (pausePackage && context.Request.Method == "GET" && context.Request.Path.StartsWithSegments("/api/v1/agent/update/packages"))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.Body.WriteAsync(new byte[128 * 1024], context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                packagePaused.TrySetResult();
                try { await packageReleased.Task.WaitAsync(context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                return;
            }
            if (pauseAssignment is { } condition && context.Request.Method == "GET" &&
                context.Request.Path == "/api/v1/agent/version-assignment" && await condition(context.RequestAborted))
            {
                assignmentPaused.TrySetResult();
                try { await assignmentReleased.Task.WaitAsync(context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            }
            if (rejectSettings && context.Request.Method == "GET" && context.Request.Path == "/api/v1/agent/settings")
            {
                Interlocked.Increment(ref settingsRejections);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
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
