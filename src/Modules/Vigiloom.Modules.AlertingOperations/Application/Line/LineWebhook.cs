using Line.OpenApi.Messaging.Webhook;
using Line.OpenApi.Messaging.Webhook.Generated.Models;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

/// <summary>Verifies LINE webhooks and extracts group candidates without changing notification settings.</summary>
public static class LineWebhook
{
    public static async Task<IReadOnlyList<string>> ReadGroupsAsync(string channelSecret, byte[] body,
        string? signature, CancellationToken cancellationToken)
    {
        var callback = await WebhookRequestParser.ParseAsync(channelSecret, body, signature, cancellationToken);
        if (callback.Events is null) return [];

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in callback.Events)
        {
            if (item is not (JoinEvent or MessageEvent) || item.Source is not GroupSource { GroupId: { Length: > 0 and <= 128 } groupId }) continue;
            groups.Add(groupId);
        }
        return [.. groups];
    }
}
