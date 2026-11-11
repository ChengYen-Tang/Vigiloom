using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Vigiloom.Agent.Platforms.Linux.Inventory;

public sealed record LinuxSystemDetails(string? DistributionId, string? DistributionVersion, DateTimeOffset? BootedAt, bool Complete);

/// <summary>Reads local distribution metadata and the kernel's boot epoch, without invoking a shell.</summary>
public static class SystemDetailsReader
{
    [SupportedOSPlatform("linux")]
    public static async Task<LinuxSystemDetails> ReadAsync(CancellationToken token)
    {
        string? distribution = null;
        string? version = null;
        DateTimeOffset? boot = null;
        var releaseValid = false;
        try
        {
            var path = File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release";
            var text = await ReadBoundedAsync(path, 64 * 1024, token);
            var parsed = ParseRelease(text);
            (distribution, version, releaseValid) = (parsed.Id, parsed.Version, !parsed.Invalid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException) { }
        try
        {
            var text = await ReadBoundedAsync("/proc/stat", 1024 * 1024, token);
            boot = ParseBoot(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException) { }
        return new(distribution, version, boot, releaseValid && distribution is not null && boot is not null);
    }

    internal static (string? Id, string? Version, bool Invalid) ParseRelease(string content)
    {
        string? id = null, version = null;
        var invalid = false;
        using var lines = new StringReader(content);
        while (lines.ReadLine() is { } line)
        {
            var split = line.IndexOf('=');
            if (split < 1) continue;
            var key = line[..split];
            if (key is not ("ID" or "VERSION_ID")) continue;
            var value = ParseValue(line[(split + 1)..]);
            if (value is null) invalid = true;
            if (key == "ID") id = value;
            else version = value;
        }
        return (id, version, invalid);
    }

    private static string? ParseValue(string value)
    {
        if (value.Length > 0 && (value[0] is '"' or '\'') && (value.Length < 2 || value[^1] != value[0])) return null;
        if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0])
        {
            var quote = value[0];
            value = value[1..^1];
            if (quote == '"')
            {
                var result = new StringBuilder(value.Length);
                for (var i = 0; i < value.Length; i++)
                {
                    if (value[i] == '\\' && i + 1 < value.Length && (value[i + 1] is '"' or '\\' or '$' or '`')) i++;
                    result.Append(value[i]);
                }
                value = result.ToString();
            }
        }
        return string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) ? null : value;
    }

    internal static DateTimeOffset? ParseBoot(string content)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("btime ", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(6).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        return null;
    }

    private static async Task<string> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await file.ReadAsync(buffer, token);
            if (count == 0) break;
            if (memory.Length + count > maximum) throw new InvalidDataException("System metadata exceeds its limit.");
            await memory.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
    }
}
