using Line.OpenApi.Messaging.Webhook;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

/// <summary>Validates a LINE webhook and records discovered group candidates.</summary>
public sealed class LineWebhookReceiver(LineChannel channel)
{
    public const int MaximumBytes = 1024 * 1024;

    public async Task<int> ReceiveAsync(Stream input, long? length, string? signature, CancellationToken token)
    {
        if (length > MaximumBytes) return 413;
        var secret = await channel.GetChannelSecretAsync(token);
        if (secret is null) return 503;
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (body.Length + read > MaximumBytes) return 413;
            body.Write(buffer, 0, read);
        }
        try
        {
            var groups = await LineWebhook.ReadGroupsAsync(secret, body.ToArray(), signature, token);
            await channel.RecordCandidatesAsync(groups, token);
            return 200;
        }
        catch (WebhookSignatureException) { return 401; }
        catch (WebhookPayloadException) { return 400; }
    }
}
