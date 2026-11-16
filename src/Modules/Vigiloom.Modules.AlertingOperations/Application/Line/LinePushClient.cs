using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

public enum LinePushResult { Accepted, Retry, Rejected }

/// <summary>Sends a single push request using LINE's stable retry key.</summary>
public sealed class LinePushClient(HttpClient http)
{
    public async Task<LinePushResult> SendAsync(string accessToken, string recipient, string text, Guid retryKey, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.line.me/v2/bot/message/push");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Line-Retry-Key", retryKey.ToString("D"));
        request.Content = JsonContent.Create(new { to = recipient, messages = new[] { new { type = "text", text } } });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict &&
            response.Headers.Contains("X-Line-Accepted-Request-Id")) return LinePushResult.Accepted;
        return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500
            ? LinePushResult.Retry : LinePushResult.Rejected;
    }
}
