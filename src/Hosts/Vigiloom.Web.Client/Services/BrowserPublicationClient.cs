using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Publishing;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserPublicationClient(HttpClient client) : IPublicationClient
{
    public Task<PublicationSettings?> GetAsync(string kind, Guid id, CancellationToken token) => ReadAsync<PublicationSettings>(Path(kind, id), token);
    public Task<PublicationPreview?> PreviewAsync(string kind, Guid id, CancellationToken token) => ReadAsync<PublicationPreview>(Path(kind, id) + "/preview", token);
    public Task<PublicationTemplatePage> TemplatesAsync(int page, CancellationToken token) => ReadRequiredAsync<PublicationTemplatePage>($"api/v1/admin/publications/templates?page={page}", token);
    public Task<PublicationTemplateItem?> TemplateAsync(Guid id, CancellationToken token) => ReadAsync<PublicationTemplateItem>($"api/v1/admin/publications/templates/{id:D}", token);
    public Task<PublicationTemplateItem?> DefaultTemplateAsync(CancellationToken token) => ReadAsync<PublicationTemplateItem>("api/v1/admin/publications/templates/default", token);

    public async Task<PublicationSaveResult> SaveAsync(string kind, Guid id, PublicationChange change, CancellationToken token)
    {
        var antiforgery = await ReadRequiredAsync<AntiforgeryToken>("api/v1/session/antiforgery", token);
        using var request = new HttpRequestMessage(HttpMethod.Put, Path(kind, id)) { Content = JsonContent.Create(change) };
        request.Headers.Add("RequestVerificationToken", antiforgery.RequestToken);
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        if (response.StatusCode == HttpStatusCode.NotFound) return new(PublicationSaveStatus.NotFound);
        if (response.StatusCode == HttpStatusCode.Conflict) return new(PublicationSaveStatus.Conflict);
        if (response.StatusCode == HttpStatusCode.BadRequest) return new(PublicationSaveStatus.Invalid);
        response.EnsureSuccessStatusCode();
        return new(PublicationSaveStatus.Updated, await response.Content.ReadFromJsonAsync<PublicationSettings>(cancellationToken: token));
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token);
    }

    private async Task<T> ReadRequiredAsync<T>(string path, CancellationToken token) =>
        await ReadAsync<T>(path, token) ?? throw new HttpRequestException("Empty response.");
    private static string Path(string kind, Guid id) => $"api/v1/admin/publications/{Uri.EscapeDataString(kind)}/{id:D}";
    private sealed record AntiforgeryToken(string RequestToken);
}
