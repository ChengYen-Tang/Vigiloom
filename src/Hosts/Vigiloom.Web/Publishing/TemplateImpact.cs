using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Builds a bounded preview and a fingerprint over every assigned device.</summary>
internal static class TemplateImpact
{
    internal const int PageSize = 50;
    internal const int MaximumDevices = 10_000;

    // Callers committing a change must hold the publication mutation lock while re-reading
    // and comparing this value; the preview alone is not a concurrency guarantee.
    internal static async Task<Result?> ReadAsync(ResourcesDbContext db, Guid templateId, byte[] templateVersion,
        int page, CancellationToken token)
    {
        if (templateId.Version != 7 || templateVersion.Length != 8 || page < 1 || page > (MaximumDevices + PageSize - 1) / PageSize)
            throw new ArgumentOutOfRangeException(nameof(page));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var idBytes = new byte[16];
        templateId.TryWriteBytes(idBytes);
        hash.AppendData(idBytes);
        hash.AppendData(templateVersion);
        var items = new List<Item>(PageSize);
        var count = 0;
        var publicCount = 0;
        var offset = (page - 1) * PageSize;
        var query = db.Devices.AsNoTracking().Where(x => x.PublicTemplateId == templateId && !x.IsArchived)
            .OrderBy(x => x.RowId).Take(MaximumDevices + 1)
            .Select(x => new { x.Id, x.Name, x.IsPublic, x.PublicPolicyVersion, x.Version });
        await foreach (var device in query.AsAsyncEnumerable().WithCancellation(token))
        {
            if (count == MaximumDevices) return null;
            device.Id.TryWriteBytes(idBytes);
            hash.AppendData(idBytes);
            hash.AppendData(device.Version);
            var isPublic = device.IsPublic && device.PublicPolicyVersion == PublicationPolicy.TemplateVersion;
            if (isPublic) publicCount++;
            if (count >= offset && items.Count < PageSize) items.Add(new(device.Id, device.Name, isPublic));
            count++;
        }
        return new(count, publicCount, page, items, Convert.ToBase64String(hash.GetHashAndReset()));
    }

    internal sealed record Item(Guid Id, string Name, bool Public);
    internal sealed record Result(int Count, int PublicCount, int Page, IReadOnlyList<Item> Items, string Fingerprint);
}
