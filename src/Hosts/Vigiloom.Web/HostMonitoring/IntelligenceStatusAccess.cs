using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class IntelligenceStatusAccess(IntelligenceStatus legacy, IHttpClientFactory clients, IConfiguration configuration)
{
    // Keep the management page on one data source until its write operations also move.
    private bool Remote => configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    private static string Source(Guid id) => $"internal/v1/intelligence/sources/{id:D}";
    private static string Revision(Guid sourceId, Guid revisionId) => Source(sourceId) + $"/revisions/{revisionId:D}";
    private static string Paged(string path, int page) => QueryHelpers.AddQueryString(path, "page", page.ToString());

    public async Task<IntelligenceSourcePage> GetSourcesAsync(int page, CancellationToken token) => Remote
        ? await ReadAsync<IntelligenceSourcePage>(Paged("internal/v1/intelligence/sources/", page), token) ?? throw new InvalidOperationException("Security returned no source list.")
        : await legacy.GetSourcesAsync(page, token);
    public Task<IntelligenceRevisionPage?> GetRevisionsAsync(Guid sourceId, int page, CancellationToken token) => Remote
        ? ReadAsync<IntelligenceRevisionPage>(Paged(Source(sourceId) + "/revisions", page), token) : legacy.GetRevisionsAsync(sourceId, page, token);
    public Task<AdvisoryImportProgressView?> GetAdvisoryProgressAsync(Guid sourceId, CancellationToken token) => Remote
        ? ReadAsync<AdvisoryImportProgressView>(Source(sourceId) + "/advisory-progress", token) : legacy.GetAdvisoryProgressAsync(sourceId, token);
    public Task<SigmaRulePage?> GetSigmaRulesAsync(Guid sourceId, Guid revisionId, int page, CancellationToken token) => Remote
        ? ReadAsync<SigmaRulePage>(Paged(Revision(sourceId, revisionId) + "/sigma-rules", page), token) : legacy.GetSigmaRulesAsync(sourceId, revisionId, page, token);
    public Task<SignatureFilePage?> GetSignatureFilesAsync(Guid sourceId, Guid revisionId, int page, CancellationToken token) => Remote
        ? ReadAsync<SignatureFilePage>(Paged(Revision(sourceId, revisionId) + "/signature-files", page), token) : legacy.GetSignatureFilesAsync(sourceId, revisionId, page, token);
    public Task<IocIssuePage?> GetIocIssuesAsync(Guid sourceId, Guid revisionId, string path, int page, CancellationToken token) => Remote
        ? ReadAsync<IocIssuePage>(QueryHelpers.AddQueryString(Revision(sourceId, revisionId) + "/ioc-issues", new Dictionary<string, string?> { ["path"] = path, ["page"] = page.ToString() }), token)
        : legacy.GetIocIssuesAsync(sourceId, revisionId, path, page, token);
    public Task<SignatureEvidenceView?> GetSignatureEvidenceAsync(Guid sourceId, Guid revisionId, string path, CancellationToken token, int? contextLine = null, int page = 1) => Remote
        ? ReadAsync<SignatureEvidenceView>(QueryHelpers.AddQueryString(Revision(sourceId, revisionId) + "/signature-evidence", new Dictionary<string, string?> { ["path"] = path, ["page"] = page.ToString(), ["line"] = contextLine?.ToString() }), token)
        : legacy.GetSignatureEvidenceAsync(sourceId, revisionId, path, token, contextLine, page);

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid intelligence query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty intelligence response.");
    }
}
