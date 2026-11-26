using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class SigmaRuleAccess(SigmaRuleInventory inventory, SigmaRuleService rules, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    public Task<SigmaDetectionRulePage> GetAsync(string? cursor, CancellationToken token) => Remote
        ? ReadAsync<SigmaDetectionRulePage>(cursor is null ? "internal/v1/sigma/rules" : QueryHelpers.AddQueryString("internal/v1/sigma/rules", "cursor", cursor), token)
        : inventory.GetAsync(cursor, token);
    public Task<SigmaRuleStatePage> GetStatesAsync(int page, CancellationToken token) => Remote
        ? ReadAsync<SigmaRuleStatePage>($"internal/v1/sigma/states?page={page}", token) : inventory.GetStatesAsync(page, token);
    public async Task<(SigmaRuleTestStatus Status, SigmaRuleTestResponse? Result)> TestAsync(SigmaRuleTestRequest input, Guid actor, CancellationToken token)
    {
        if (!Remote) return await rules.TestAsync(input, actor, token);
        var response = await SendAsync<TestResult>(HttpMethod.Post, "tests", input, actor, token);
        return (response.Status, response.Result);
    }
    public Task<SigmaRuleChangeStatus> SetAsync(SigmaRuleStateRequest input, Guid actor, CancellationToken token) => Remote
        ? SendAsync<SigmaRuleChangeStatus>(HttpMethod.Put, "state", input, actor, token) : rules.SetAsync(input, actor, token);

    private async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid Sigma query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new InvalidOperationException("Security returned an empty Sigma response.");
    }
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object input, Guid actor, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, "internal/v1/sigma/" + path) { Content = JsonContent.Create(new { Input = input, ActorId = actor }) };
        using var response = await clients.CreateClient("security").SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid Sigma request.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new InvalidOperationException("Security returned an empty Sigma result.");
    }
    private sealed record TestResult(SigmaRuleTestStatus Status, SigmaRuleTestResponse? Result);
}
