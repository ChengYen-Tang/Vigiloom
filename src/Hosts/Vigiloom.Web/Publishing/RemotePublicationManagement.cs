using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Publishing;

internal sealed class RemotePublicationManagement(HttpClient client)
{
    private const string Root = "internal/v1/publication-management/";

    public Task<PublicationSettings?> GetAsync(string kind, Guid id, CancellationToken token) => GetAsync<PublicationSettings>($"{kind}/{id:D}", token);
    public Task<PublicationTemplateItem?> TemplateAsync(Guid id, CancellationToken token) => GetAsync<PublicationTemplateItem>($"templates/{id:D}", token);
    public Task<PublicationTemplateItem?> DefaultAsync(CancellationToken token) => GetAsync<PublicationTemplateItem>("templates/default", token);
    public async Task<PublicationTemplatePage> TemplatesAsync(int page, CancellationToken token) =>
        await GetAsync<PublicationTemplatePage>($"templates?page={page}", token) ?? throw new InvalidOperationException("The template list was not found.");
    public async Task<TemplateOperationResult<PublicationImpact>> ImpactAsync(Guid id, int page, CancellationToken token) =>
        await GetAsync<TemplateOperationResult<PublicationImpact>>($"templates/{id:D}/impact?page={page}", token) ?? throw new InvalidOperationException("The template impact was not found.");
    public Task<PublicationSaveResult> SaveAsync(string kind, Guid id, PublicationChange input, Guid actor, CancellationToken token) =>
        SendAsync<PublicationChange, PublicationSaveResult>(HttpMethod.Put, $"{kind}/{id:D}", input, actor, token);
    public Task<TemplateOperationResult<PublicationTemplateItem>> CreateAsync(CreatePublicationTemplate input, Guid actor, CancellationToken token) =>
        SendAsync<CreatePublicationTemplate, TemplateOperationResult<PublicationTemplateItem>>(HttpMethod.Post, "templates", input, actor, token);
    public Task<TemplateOperationResult<PublicationTemplateItem>> SetDefaultAsync(Guid id, SetDefaultPublicationTemplate input, Guid actor, CancellationToken token) =>
        SendAsync<SetDefaultPublicationTemplate, TemplateOperationResult<PublicationTemplateItem>>(HttpMethod.Put, $"templates/{id:D}/default", input, actor, token);
    public Task<TemplateOperationResult<PublicationTemplateUpdatePreview>> PreviewUpdateAsync(Guid id, PublicationTemplateUpdate input, Guid actor, CancellationToken token) =>
        SendAsync<PublicationTemplateUpdate, TemplateOperationResult<PublicationTemplateUpdatePreview>>(HttpMethod.Post, $"templates/{id:D}/update-preview", input, actor, token);
    public Task<TemplateOperationResult<PublicationTemplateItem>> ConfirmUpdateAsync(Guid id, ConfirmPublicationTemplateUpdate input, Guid actor, CancellationToken token) =>
        SendAsync<ConfirmPublicationTemplateUpdate, TemplateOperationResult<PublicationTemplateItem>>(HttpMethod.Put, $"templates/{id:D}", input, actor, token);
    public Task<TemplateOperationResult<PublicationTemplateRetirementPreview>> PreviewRetireAsync(Guid id, PublicationTemplateRetirement input, Guid actor, CancellationToken token) =>
        SendAsync<PublicationTemplateRetirement, TemplateOperationResult<PublicationTemplateRetirementPreview>>(HttpMethod.Post, $"templates/{id:D}/retirement-preview", input, actor, token);
    public async Task<TemplateOperationResult<bool>> ConfirmRetireAsync(Guid id, ConfirmPublicationTemplateRetirement input, Guid actor, CancellationToken token)
    {
        var status = await SendAsync<ConfirmPublicationTemplateRetirement, TemplateOperationStatus>(HttpMethod.Post, $"templates/{id:D}/retire", input, actor, token);
        return new(status, status == TemplateOperationStatus.Success);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(Root + path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new InvalidOperationException("The resource service returned an empty response.");
    }

    private async Task<TOutput> SendAsync<TInput, TOutput>(HttpMethod method, string path, TInput input, Guid actor, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, Root + path) { Content = JsonContent.Create(new PublicationManagementRequest<TInput>(input, actor)) };
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TOutput>(cancellationToken: token) ?? throw new InvalidOperationException("The resource service returned an empty response.");
    }
}
