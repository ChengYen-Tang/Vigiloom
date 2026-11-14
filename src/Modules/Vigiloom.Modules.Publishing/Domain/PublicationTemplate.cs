namespace Vigiloom.Modules.Publishing.Domain;

/// <summary>A reusable device publication policy; it does not itself publish a device.</summary>
public sealed class PublicationTemplate
{
    public static readonly DateTimeOffset PendingHistorySince = DateTimeOffset.MaxValue;
    private PublicationTemplate() { }

    public PublicationTemplate(string name, bool showLatency, bool showObservedAt, DateTimeOffset now, int historyDays = 0)
    {
        Id = Guid.CreateVersion7();
        Name = ValidateName(name);
        ShowLatency = showLatency;
        ShowObservedAt = showObservedAt;
        SetHistory(historyDays);
        CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public string Name { get; private set; } = null!;
    public bool ShowLatency { get; private set; }
    public bool ShowObservedAt { get; private set; }
    public int HistoryDays { get; private set; }
    public DateTimeOffset? HistorySince { get; private set; }
    public bool IsDefault { get; private set; }
    public bool IsRetired { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void Update(string name, bool showLatency, bool showObservedAt, DateTimeOffset now) =>
        Update(name, showLatency, showObservedAt, now, HistoryDays);

    public void Update(string name, bool showLatency, bool showObservedAt, DateTimeOffset now, int historyDays)
    {
        if (IsRetired) throw new InvalidOperationException("A retired template cannot be edited.");
        if (historyDays is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(historyDays));
        Name = ValidateName(name);
        ShowLatency = showLatency;
        ShowObservedAt = showObservedAt;
        SetHistory(historyDays);
        UpdatedAt = now.ToUniversalTime();
    }

    private void SetHistory(int days)
    {
        if (days is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(days));
        if (days == 0) HistorySince = null;
        else if (days > HistoryDays) HistorySince = PendingHistorySince;
        HistoryDays = days;
    }

    public void ConfirmHistorySince(DateTimeOffset committedAfter)
    {
        if (IsRetired || HistoryDays == 0 || HistorySince != PendingHistorySince || committedAfter.Offset != TimeSpan.Zero || committedAfter >= PendingHistorySince)
            throw new InvalidOperationException("The template history period is not pending.");
        HistorySince = committedAfter;
    }

    public void SetDefault(bool value, DateTimeOffset now)
    {
        if (value && IsRetired) throw new InvalidOperationException("A retired template cannot be the default.");
        IsDefault = value;
        UpdatedAt = now.ToUniversalTime();
    }

    public void Retire(DateTimeOffset now)
    {
        IsRetired = true;
        IsDefault = false;
        UpdatedAt = now.ToUniversalTime();
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 256 || trimmed.Any(char.IsControl))
            throw new ArgumentException("A template name must be 1 to 256 characters without control characters.", nameof(name));
        return trimmed;
    }
}
