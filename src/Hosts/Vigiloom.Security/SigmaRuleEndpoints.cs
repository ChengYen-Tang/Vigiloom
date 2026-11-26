using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Security;

internal static class SigmaRuleEndpoints
{
    public static void MapSigmaRuleEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/sigma");
        group.MapGet("/rules", async (string? cursor, IServiceProvider services, CancellationToken token) =>
        {
            if (!SigmaRuleInventory.ValidCursor(cursor)) return Results.BadRequest();
            var rules = services.GetService<SigmaRuleInventory>();
            return rules is null ? Results.StatusCode(503) : Results.Ok(await rules.GetAsync(cursor, token));
        });
        group.MapGet("/states", async (int page, IServiceProvider services, CancellationToken token) =>
        {
            if (page is < 1 or > 1000) return Results.BadRequest();
            var rules = services.GetService<SigmaRuleInventory>();
            return rules is null ? Results.StatusCode(503) : Results.Ok(await rules.GetStatesAsync(page, token));
        });
        group.MapPost("/tests", async (TestCommand command, IServiceProvider services, CancellationToken token) =>
        {
            if (!SigmaRuleService.Valid(command.Input)) return Results.BadRequest();
            var rules = services.GetService<SigmaRuleService>();
            if (rules is null) return Results.StatusCode(503);
            var result = await rules.TestAsync(command.Input, command.ActorId, token);
            return Results.Ok(new TestResult(result.Status, result.Result));
        });
        group.MapPut("/state", async (StateCommand command, IServiceProvider services, CancellationToken token) =>
        {
            if (!SigmaRuleService.Valid(command.Input)) return Results.BadRequest();
            var rules = services.GetService<SigmaRuleService>();
            return rules is null ? Results.StatusCode(503) : Results.Ok(await rules.SetAsync(command.Input, command.ActorId, token));
        });
    }

    private sealed record TestCommand(SigmaRuleTestRequest Input, Guid ActorId);
    private sealed record StateCommand(SigmaRuleStateRequest Input, Guid ActorId);
    private sealed record TestResult(SigmaRuleTestStatus Status, SigmaRuleTestResponse? Result);
}
