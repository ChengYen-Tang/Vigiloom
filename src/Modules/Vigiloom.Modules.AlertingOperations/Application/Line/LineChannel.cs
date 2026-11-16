using System.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

public sealed record LineGroupView(Guid Id, string ExternalId, string? Name, bool Approved, DateTimeOffset LastSeenAt);
public sealed record LineChannelView(bool Enabled, bool HasAccessToken, bool HasChannelSecret, IReadOnlyList<LineGroupView> Groups);
public sealed record LineChannelUpdate(bool Enabled, string? AccessToken, string? ChannelSecret);

/// <summary>Stores channel credentials and groups; webhook discovery never approves a group.</summary>
public sealed class LineChannel(IDbContextFactory<OperationsDbContext> contexts, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("Vigiloom.Line.Channel.v1");

    public async Task<LineChannelView> GetAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var settings = await db.LineSettings.AsNoTracking().SingleOrDefaultAsync(token);
        var groups = await db.LineGroups.AsNoTracking().OrderByDescending(x => x.LastSeenAt)
            .Select(x => new LineGroupView(x.Id, x.ExternalId, x.Name, x.Approved, x.LastSeenAt)).ToArrayAsync(token);
        return new(settings?.Enabled ?? false, settings?.ProtectedAccessToken is not null,
            settings?.ProtectedChannelSecret is not null, groups);
    }

    public async Task<bool> SaveAsync(LineChannelUpdate update, CancellationToken token)
    {
        if (update.AccessToken is { Length: > 4096 } || update.ChannelSecret is { Length: > 4096 }) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var settings = await db.LineSettings.SingleOrDefaultAsync(token);
        if (settings is null) { settings = new LineSettings(); db.LineSettings.Add(settings); }
        if (!string.IsNullOrWhiteSpace(update.AccessToken)) settings.ProtectedAccessToken = protector.Protect(update.AccessToken.Trim());
        if (!string.IsNullOrWhiteSpace(update.ChannelSecret)) settings.ProtectedChannelSecret = protector.Protect(update.ChannelSecret.Trim());
        if (update.Enabled && (settings.ProtectedAccessToken is null || settings.ProtectedChannelSecret is null)) return false;
        if (update.Enabled && !settings.Enabled)
            settings.LastNotificationRowId = await db.Notifications.MaxAsync(x => (long?)x.RowId, token) ?? 0;
        settings.Enabled = update.Enabled;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task<bool> SetGroupAsync(Guid id, string? name, bool approved, CancellationToken token)
    {
        if (name is { Length: > 256 }) return false;
        await using var db = await contexts.CreateDbContextAsync(token);
        var group = await db.LineGroups.SingleOrDefaultAsync(x => x.Id == id, token);
        if (group is null) return false;
        group.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        group.Approved = approved;
        await db.SaveChangesAsync(token);
        return true;
    }

    public async Task<string?> GetChannelSecretAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var secret = await db.LineSettings.AsNoTracking().Select(x => x.ProtectedChannelSecret).SingleOrDefaultAsync(token);
        return secret is null ? null : protector.Unprotect(secret);
    }

    public async Task RecordCandidatesAsync(IReadOnlyList<string> candidates, CancellationToken token)
    {
        if (candidates.Count == 0) return;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var existing = await db.LineGroups.Where(x => candidates.Contains(x.ExternalId)).ToDictionaryAsync(x => x.ExternalId, token);
        var now = clock.GetUtcNow();
        foreach (var externalId in candidates)
        {
            if (existing.TryGetValue(externalId, out var group)) group.LastSeenAt = now;
            else db.LineGroups.Add(new LineGroup { ExternalId = externalId, LastSeenAt = now });
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
