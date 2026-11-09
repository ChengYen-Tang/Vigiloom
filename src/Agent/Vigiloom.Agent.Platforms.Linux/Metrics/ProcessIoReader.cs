using System.Globalization;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

public static class ProcessIoReader
{
    [SupportedOSPlatform("linux")]
    public static async Task<ProcessIoSnapshot?> ReadAsync(CancellationToken token) =>
        Parse(await File.ReadAllTextAsync("/proc/self/io", token));

    public static ProcessIoSnapshot? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong? read = null, write = null;
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            var split = line.IndexOf(':');
            if (split < 0) continue;
            var name = line.AsSpan(0, split);
            if (!name.SequenceEqual("rchar") && !name.SequenceEqual("wchar")) continue;
            if (!ulong.TryParse(line.AsSpan(split + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            if (name.SequenceEqual("rchar")) { if (read is not null) return null; read = value; }
            else { if (write is not null) return null; write = value; }
        }
        return read is { } r && write is { } w ? new(r, w) : null;
    }
}
