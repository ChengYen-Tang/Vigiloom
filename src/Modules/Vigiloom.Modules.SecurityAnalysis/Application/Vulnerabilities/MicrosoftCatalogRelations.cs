using System.Globalization;
using Poushec.UpdateCatalogParser;
using Poushec.UpdateCatalogParser.Exceptions;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum CatalogRelationStatus { Ready, Unsupported, Unavailable, Ambiguous }
internal sealed record CatalogRelationResult(CatalogRelationStatus Status, string? UpdateId = null, IReadOnlyList<string>? SupersededKbs = null);

/// <summary>Loads product-scoped cumulative update relations from Microsoft Update Catalog.</summary>
internal sealed class MicrosoftCatalogRelations(IHttpClientFactory clients)
{
    internal async Task<CatalogRelationResult> GetAsync(string product, string kb, CancellationToken token)
    {
        var scope = Scope(product);
        if (scope is null)
            return new(CatalogRelationStatus.Unsupported);
        if (!ValidKb(kb)) throw new ArgumentException("A numeric KB identifier is required.", nameof(kb));
        using var http = clients.CreateClient(IntelligenceHttp.MicrosoftCatalogClientName);
        http.Timeout = TimeSpan.FromSeconds(30);
        http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        var client = new CatalogClient(http, CultureInfo.GetCultureInfo("en-US"), 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var page = await client.GetFirstPageFromSearchQueryAsync($"KB{kb}", cancellationToken: deadline.Token);
            if (!page.FinalPage) return new(CatalogRelationStatus.Ambiguous);
            var matching = page.SearchResults.Where(x => string.Equals(x.Products, scope.Value.CatalogProduct, StringComparison.OrdinalIgnoreCase) &&
                TryTitleKb(x.Title, scope.Value.TitleProduct, out var id) && id == kb).Take(2).ToArray();
            if (matching.Length == 0) return new(CatalogRelationStatus.Unavailable);
            if (matching.Length != 1 || !Guid.TryParse(matching[0].UpdateID, out var updateId) || updateId == Guid.Empty)
                return new(CatalogRelationStatus.Ambiguous);
            var detail = await client.GetUpdateDetailsAsync(matching[0], deadline.Token);
            var properties = detail.AdditionalProperties;
            if (properties is null || !string.Equals(properties.KBArticleNumbers, kb, StringComparison.Ordinal) ||
                properties.Supersedes.Count is 0 or > 5000) return new(CatalogRelationStatus.Unavailable);
            var relations = new HashSet<string>(StringComparer.Ordinal);
            foreach (var title in properties.Supersedes)
            {
                if (!TryTitleKb(title, scope.Value.TitleProduct, out var superseded)) return new(CatalogRelationStatus.Ambiguous);
                relations.Add(superseded);
            }
            return new(CatalogRelationStatus.Ready, updateId.ToString("D"), relations.Order(StringComparer.Ordinal).ToArray());
        }
        catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException or FormatException or
            CatalogErrorException or CatalogFailedToLoadSearchResultsPageException or CatalogNoResultsException or ParseHtmlPageException or
            RequestToCatalogTimedOutException or UnableToCollectUpdateDetailsException or UpdateWasNotFoundException)
        { return new(CatalogRelationStatus.Unavailable); }
    }

    internal static (string CatalogProduct, string TitleProduct)? Scope(string product)
    {
        const string core = " (Server Core installation)";
        if (product.EndsWith(core, StringComparison.Ordinal)) product = product[..^core.Length];
        return product switch
        {
            "Windows Server 2016" => ("Windows Server 2016", "Windows Server 2016"),
            "Windows Server 2019" => ("Windows Server 2019", "Windows Server 2019"),
            "Windows Server 2022" => ("Microsoft Server operating system-21H2", "Microsoft server operating system version 21H2"),
            "Windows Server 2025" => ("Microsoft Server operating system-24H2", "Microsoft server operating system version 24H2"),
            _ => null
        };
    }

    internal static bool TryTitleKb(string? title, out string kb) => TryTitleKb(title, "Microsoft server operating system version 21H2", out kb);

    internal static bool TryTitleKb(string? title, string titleProduct, out string kb)
    {
        kb = "";
        if (title is null || title.Length > 2048 || !title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase) ||
            !title.Contains($"for {titleProduct} for x64-based Systems", StringComparison.OrdinalIgnoreCase)) return false;
        var index = title.LastIndexOf("(KB", StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var end = title.IndexOf(')', index + 3);
        if (end < 0) return false;
        var suffix = title.AsSpan(end + 1);
        if (!suffix.IsEmpty && (suffix.Length < 5 || !suffix.StartsWith(" (", StringComparison.Ordinal) || suffix[^1] != ')' ||
            suffix[2..^1].IndexOfAnyExcept("0123456789.") >= 0 || !suffix[2..^1].Contains('.'))) return false;
        var value = title.AsSpan(index + 3, end - index - 3);
        if (value.Length is < 6 or > 10 || value.IndexOfAnyExceptInRange('0', '9') >= 0) return false;
        kb = value.ToString();
        return true;
    }

    private static bool ValidKb(string kb) => kb is { Length: >= 6 and <= 10 } && kb.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;
}

/// <summary>Restricts the parser's requests to the fixed Microsoft Catalog origin.</summary>
internal sealed class MicrosoftCatalogHostGuard : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.IdnHost, "www.catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo)) throw new HttpRequestException("Unexpected Microsoft Catalog destination.");
        return base.SendAsync(request, cancellationToken);
    }
}
