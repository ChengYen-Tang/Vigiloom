namespace Vigiloom.Web.Telemetry;

/// <summary>Bounds OTLP work, including authentication and body read, before entering the endpoint.</summary>
internal sealed class OtlpTransportGuard(RequestDelegate next, OtlpConcurrency concurrency)
{
    public Task InvokeAsync(HttpContext context) => context.Request.Path.StartsWithSegments(OtlpHttpEndpoints.Prefix)
        ? ProcessAsync(context, next, concurrency, TimeSpan.FromSeconds(30))
        : next(context);

    internal static async Task ProcessAsync(HttpContext context, RequestDelegate next, OtlpConcurrency concurrency, TimeSpan timeout)
    {
        var original = context.RequestAborted;
        if (!await concurrency.Gate.WaitAsync(0, original))
        {
            context.Response.Headers.RetryAfter = "1";
            await OtlpResponse.Failure(StatusCodes.Status503ServiceUnavailable, "Receiver capacity is busy; retry later.").ExecuteAsync(context);
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(original);
        deadline.CancelAfter(timeout);
        context.RequestAborted = deadline.Token;
        try
        {
            try { await next(context); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !original.IsCancellationRequested && !context.Response.HasStarted)
            {
                context.RequestAborted = original;
                context.Response.Headers.RetryAfter = "1";
                await OtlpResponse.Failure(StatusCodes.Status503ServiceUnavailable, "Receiver deadline exceeded; retry later.").ExecuteAsync(context);
            }
        }
        finally
        {
            context.RequestAborted = original;
            concurrency.Gate.Release();
        }
    }
}
