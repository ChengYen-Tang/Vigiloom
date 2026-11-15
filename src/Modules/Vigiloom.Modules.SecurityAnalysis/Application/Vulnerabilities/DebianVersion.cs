namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

/// <summary>Debian policy version ordering. Invalid or unbounded input is not comparable.</summary>
internal static class DebianVersion
{
    private const int MaximumLength = 2048;

    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParts(left, out var a) || !TryParts(right, out var b)) return false;
        comparison = CompareDigits(a.Epoch, b.Epoch);
        if (comparison == 0) comparison = ComparePart(a.Upstream, b.Upstream);
        if (comparison == 0) comparison = ComparePart(a.Revision, b.Revision);
        return true;
    }

    private static bool TryParts(string? text, out Parts result)
    {
        result = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaximumLength) return false;
        var value = text.AsSpan();
        var colon = value.IndexOf(':');
        var epoch = colon < 0 ? "0".AsSpan() : value[..colon];
        var remainder = colon < 0 ? value : value[(colon + 1)..];
        if (epoch.IsEmpty || remainder.IsEmpty || !IsDigits(epoch) || !IsDigit(remainder[0])) return false;
        var dash = remainder.LastIndexOf('-');
        var upstream = dash < 0 ? remainder : remainder[..dash];
        var revision = dash < 0 ? "0".AsSpan() : remainder[(dash + 1)..];
        if (upstream.IsEmpty || revision.IsEmpty) return false;
        foreach (var c in upstream)
            if (!(IsLetter(c) || IsDigit(c) || c is '.' or '+' or '-' or '~')) return false;
        foreach (var c in revision)
            if (!(IsLetter(c) || IsDigit(c) || c is '.' or '+' or '~')) return false;
        result = new(epoch, upstream, revision);
        return true;
    }

    private static int ComparePart(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var a = 0; var b = 0;
        while (a < left.Length || b < right.Length)
        {
            while (a < left.Length && !IsDigit(left[a]) || b < right.Length && !IsDigit(right[b]))
            {
                var x = a < left.Length && !IsDigit(left[a]) ? Order(left[a]) : 0;
                var y = b < right.Length && !IsDigit(right[b]) ? Order(right[b]) : 0;
                if (x != y) return x.CompareTo(y);
                if (a < left.Length && !IsDigit(left[a])) a++;
                if (b < right.Length && !IsDigit(right[b])) b++;
            }
            var startA = a; var startB = b;
            while (a < left.Length && IsDigit(left[a])) a++;
            while (b < right.Length && IsDigit(right[b])) b++;
            var digits = CompareDigits(left[startA..a], right[startB..b]);
            if (digits != 0) return digits;
        }
        return 0;
    }

    private static int CompareDigits(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        while (!left.IsEmpty && left[0] == '0') left = left[1..];
        while (!right.IsEmpty && right[0] == '0') right = right[1..];
        if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
        return left.SequenceCompareTo(right);
    }

    private static int Order(char c) => c == '~' ? -1 : IsLetter(c) ? c : c + 256;
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
    private static bool IsLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool IsDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value) if (!IsDigit(c)) return false;
        return true;
    }

    private readonly ref struct Parts(ReadOnlySpan<char> epoch, ReadOnlySpan<char> upstream, ReadOnlySpan<char> revision)
    {
        public ReadOnlySpan<char> Epoch { get; } = epoch;
        public ReadOnlySpan<char> Upstream { get; } = upstream;
        public ReadOnlySpan<char> Revision { get; } = revision;
    }
}
