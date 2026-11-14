using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

internal sealed record IocLine(int Number, string Kind, string? Value, string? Reason);

/// <summary>Classifies known file formats without performing a match or executing source expressions.</summary>
internal static class IocParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static IEnumerable<IocLine> Parse(string path, byte[] content)
    {
        string text;
        try { text = StrictUtf8.GetString(content); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid IOC source encoding.", error); }
        using var reader = new StringReader(text);
        var number = 0;
        while (reader.ReadLine() is { } line)
        {
            number++;
            if (number > 100_000) throw new InvalidDataException("IOC source contains too many lines.");
            var input = line.Trim();
            if (input.Length == 0 || input.StartsWith('#')) continue;
            if (input.Length > 2048) { yield return new(number, "unsupported", null, "line-too-long"); continue; }
            if (path == "iocs/hash-iocs.txt")
            {
                var separator = input.IndexOf(';');
                var hash = (separator < 0 ? input : input[..separator]).Trim();
                var kind = hash.Length switch { 32 => "md5", 40 => "sha1", 64 => "sha256", _ => null };
                yield return kind is not null && hash.All(Uri.IsHexDigit)
                    ? new(number, kind, hash.ToLowerInvariant(), null)
                    : new(number, "unsupported", input, "invalid-hash");
            }
            else if (path == "iocs/c2-iocs.txt")
            {
                if (!input.Contains('%') && IPAddress.TryParse(input, out var address))
                {
                    var normalized = address.ToString();
                    yield return address.AddressFamily == AddressFamily.InterNetwork && input != normalized
                        ? new(number, "unsupported", input, "noncanonical-ip")
                        : new(number, "ip", normalized, null);
                }
                else if (IsDomain(input)) yield return new(number, "domain", input, null);
                else yield return new(number, "unsupported", input, "invalid-c2");
            }
            else if (path == "iocs/filename-iocs.txt") yield return new(number, "unsupported", input, "filename-regex-not-supported");
            else yield return new(number, "unsupported", input, "unrecognized-ioc-format");
        }
    }

    private static bool IsDomain(string input)
    {
        if (input.Length > 253 || Uri.CheckHostName(input) != UriHostNameType.Dns) return false;
        var labels = input.Split('.');
        return labels.Length > 1 && labels[^1].Any(char.IsAsciiLetter) && labels.All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }
}
