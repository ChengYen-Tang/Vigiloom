namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Classifies an unseen batch against central replay obligations.</summary>
public static class AgentReplayWindow
{
    public static BatchReceiptStatus? Classify(DateTimeOffset enqueuedAt, DateTimeOffset authorityTime) =>
        Classify(enqueuedAt, authorityTime, TimeSpan.FromHours(24), TimeSpan.FromMinutes(5), null);

    public static BatchReceiptStatus? Classify(DateTimeOffset enqueuedAt, DateTimeOffset authorityTime,
        TimeSpan window, TimeSpan allowedFutureSkew, DateTimeOffset? cleanedBefore)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(allowedFutureSkew, TimeSpan.Zero);
        var age = authorityTime - enqueuedAt;
        if (cleanedBefore is { } fence && enqueuedAt < fence || age > window) return BatchReceiptStatus.Expired;
        if (age < -allowedFutureSkew) return BatchReceiptStatus.TimestampAhead;
        return null;
    }

    /// <summary>Latest instant at which the old allowance may still be needed after a shortening.</summary>
    public static DateTimeOffset TransitionDeadline(DateTimeOffset changedAt, TimeSpan oldWindow, TimeSpan allowedFutureSkew)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(oldWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(allowedFutureSkew, TimeSpan.Zero);
        return changedAt.Add(oldWindow).Add(allowedFutureSkew);
    }

    /// <summary>Uses the longest still-applicable central obligation, including the deadline instant.</summary>
    public static TimeSpan EffectiveWindow(TimeSpan current, DateTimeOffset authorityTime,
        ReadOnlySpan<ReplayWindowObligation> previous)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(current, TimeSpan.Zero);
        foreach (var obligation in previous)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(obligation.Window, TimeSpan.Zero);
            if (authorityTime <= obligation.Until && obligation.Window > current) current = obligation.Window;
        }
        return current;
    }
}

public readonly record struct ReplayWindowObligation(TimeSpan Window, DateTimeOffset Until);
