using System.Web;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

/// <summary>Extracts only a GitHub cursor whose link preserves the requested query.</summary>
internal static class AdvisoryLinks
{
    internal static string? Next(IEnumerable<string> headers, string modified, bool withdrawn)
    {
        string? cursor = null;
        foreach (var link in headers.SelectMany(x => x.Split(',')))
        {
            var parts = link.Split(';', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length < 3 || parts[0][0] != '<' || parts[0][^1] != '>')
                throw new InvalidDataException("Invalid advisory link header.");
            string? relation = null;
            foreach (var parameter in parts.Skip(1))
            {
                var split = parameter.IndexOf('=');
                if (split <= 0) throw new InvalidDataException("Invalid advisory link parameter.");
                if (!parameter[..split].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase)) continue;
                if (relation is not null) throw new InvalidDataException("Duplicate advisory link relation.");
                var raw = parameter[(split + 1)..].Trim(' ');
                if (raw.StartsWith('"'))
                {
                    if (raw.Length < 2 || raw[^1] != '"') throw new InvalidDataException("Invalid quoted advisory link relation.");
                    relation = raw[1..^1];
                }
                else
                {
                    if (raw.Contains('"') || raw.Contains(' ')) throw new InvalidDataException("Invalid advisory link relation.");
                    relation = raw;
                }
                if (relation.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '.' or '_' or ':' or '/')))
                    throw new InvalidDataException("Invalid advisory link relation.");
            }
            if (string.IsNullOrWhiteSpace(relation)) throw new InvalidDataException("Missing advisory link relation.");
            if (!relation.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next", StringComparer.OrdinalIgnoreCase)) continue;
            if (cursor is not null ||
                !Uri.TryCreate(parts[0][1..^1], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/advisories")
                throw new InvalidDataException("Invalid advisory pagination link.");
            var query = HttpUtility.ParseQueryString(uri.Query);
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = "reviewed", ["per_page"] = "100", ["sort"] = "updated", ["direction"] = "asc", ["modified"] = modified
            };
            if (withdrawn) expected["is_withdrawn"] = "true";
            foreach (var pair in expected)
                if (query.GetValues(pair.Key) is not [var value] || value != pair.Value)
                    throw new InvalidDataException("Advisory pagination changed the query.");
            if (query.AllKeys.Length != expected.Count + 1 || query.GetValues("after") is not [var after] || !ValidCursor(after))
                throw new InvalidDataException("Invalid advisory pagination cursor.");
            cursor = after;
        }
        return cursor;
    }

    internal static bool ValidCursor(string? value) => value is { Length: > 0 and <= 1024 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_');
}
