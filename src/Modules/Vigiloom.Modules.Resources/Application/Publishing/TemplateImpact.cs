using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application.Publishing;

/// <summary>Summarizes affected devices and detects changes between preview and confirmation.</summary>
public static class TemplateImpact
{
    public static async Task<PublicationImpact> ReadAsync(ResourcesDbContext db, Guid templateId, byte[] version, int page, CancellationToken token)
    {
        const int pageSize = 50;
        if (page is < 1 or > int.MaxValue / pageSize) throw new ArgumentOutOfRangeException(nameof(page));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[16];
        templateId.TryWriteBytes(bytes);
        hash.AppendData(bytes);
        hash.AppendData(version);
        var items = new List<PublicationImpactItem>(pageSize);
        var count = 0;
        var publicCount = 0;
        var offset = (page - 1) * pageSize;
        var query = db.Devices.AsNoTracking().Where(d => d.PublicTemplateId == templateId && !d.IsArchived).OrderBy(d => d.RowId)
            .Select(d => new { d.Id, d.Name, d.IsPublic, d.PublicPolicyVersion, d.Version });
        await foreach (var device in query.AsAsyncEnumerable().WithCancellation(token))
        {
            device.Id.TryWriteBytes(bytes);
            hash.AppendData(bytes);
            hash.AppendData(device.Version);
            var isPublic = device.IsPublic && device.PublicPolicyVersion == PublicationPolicy.TemplateVersion;
            if (isPublic) publicCount++;
            if (count >= offset && items.Count < pageSize) items.Add(new(device.Id, device.Name, isPublic));
            count++;
        }
        return new(count, publicCount, page, items, Convert.ToBase64String(hash.GetHashAndReset()), Convert.ToBase64String(version));
    }
}
