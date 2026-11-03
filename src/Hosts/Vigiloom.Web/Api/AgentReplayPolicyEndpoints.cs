using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentReplayPolicyEndpoints
{
    public static void MapAgentReplayPolicyEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/replay-policy")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (HttpContext context, AgentReplayPolicyService policy, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.GetAsync(token));
        }).Produces<AgentReplayPolicyView>().ProducesProblem(401).ProducesProblem(403);
        admin.MapPut("/", async (AgentReplayPolicyUpdate update, HttpContext context, AgentReplayPolicyService policy, CancellationToken token) =>
        {
            if (update.Target is not (AgentReplayPhase.Grace or AgentReplayPhase.Enforced) || !update.ConfirmedReceiversUpgraded)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["請先確認所有接收節點已更新，並選擇有效的政策階段。"] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
                await policy.TransitionAsync(update.Target, update.Version, actor, token) is { } saved
                    ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "政策狀態已變更或過渡期尚未結束，請重新整理。"), allowApiCredential: false);
        }).Produces<AgentReplayPolicyView>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);
    }
}
