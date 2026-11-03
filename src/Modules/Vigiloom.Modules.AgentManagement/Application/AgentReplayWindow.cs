namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Classifies an unseen batch against the fixed initial replay policy.</summary>
public static class AgentReplayWindow
{
    public static BatchReceiptStatus? Classify(DateTimeOffset enqueuedAt, DateTimeOffset authorityTime)
    {
        var age = authorityTime - enqueuedAt;
        if (age > TimeSpan.FromHours(24)) return BatchReceiptStatus.Expired;
        if (age < -TimeSpan.FromMinutes(5)) return BatchReceiptStatus.TimestampAhead;
        return null;
    }
}
