using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Retrieves the published FIRST daily snapshot, allowing one validated same-origin dated redirect.</summary>
internal sealed class EpssClient(HttpClient client)
{
    internal async Task<byte[]> FetchAsync(int maximumBytes, CancellationToken token)
    {
        if (maximumBytes is < 1024 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var endpoint = new Uri("https://epss.empiricalsecurity.com/epss_scores-current.csv.gz");
        using var initial = await SendAsync(endpoint, timeout.Token);
        if (initial.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.MovedPermanently or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
        {
            var location = initial.Headers.Location ?? throw new InvalidDataException("EPSS redirect has no location.");
            var target = location.IsAbsoluteUri ? location : new Uri(endpoint, location);
            if (target.Scheme != Uri.UriSchemeHttps || target.Host != endpoint.Host || target.Port != 443 ||
                target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0 ||
                !ValidDatedPath(target.AbsolutePath)) throw new InvalidDataException("Unexpected EPSS redirect.");
            using var redirected = await SendAsync(target, timeout.Token);
            return await ReadAsync(redirected, maximumBytes, timeout.Token);
        }
        return await ReadAsync(initial, maximumBytes, timeout.Token);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri endpoint, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/gzip"));
        request.Headers.AcceptEncoding.ParseAdd("identity");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.RequestMessage?.RequestUri?.Host != "epss.empiricalsecurity.com")
            throw new InvalidDataException("Unexpected EPSS response.");
        var content = await BoundedHttpBody.ReadAsync(response.Content, maximumBytes, token);
        if (response.Content.Headers.ContentLength is long length && content.LongLength != length)
            throw new InvalidDataException("Truncated EPSS response.");
        return content;
    }

    private static bool ValidDatedPath(string path)
    {
        const string prefix = "/epss_scores-";
        const string suffix = ".csv.gz";
        return path.Length == prefix.Length + 10 + suffix.Length && path.StartsWith(prefix, StringComparison.Ordinal) &&
            path.EndsWith(suffix, StringComparison.Ordinal) && DateOnly.TryParseExact(path.AsSpan(prefix.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
