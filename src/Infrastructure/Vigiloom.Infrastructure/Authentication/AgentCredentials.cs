namespace Vigiloom.Infrastructure.Authentication;

/// <summary>Encodes and parses device enrollment and Agent credentials.</summary>
public static class AgentCredentials
{
    public static string Encode(string prefix, Guid id, byte[] secret) =>
        $"{prefix}_{id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    public static bool TryDecode(string? value, string prefix, out Guid id, out byte[] secret)
    {
        id = default; secret = [];
        var expected = prefix + "_";
        if (value is null || value.Length != expected.Length + 80 || !value.StartsWith(expected, StringComparison.Ordinal) ||
            value[expected.Length + 36] != '.' || !Guid.TryParseExact(value.AsSpan(expected.Length, 36), "D", out id) || id == Guid.Empty) return false;
        try { secret = Convert.FromBase64String(value[(expected.Length + 37)..].Replace('-', '+').Replace('_', '/') + "="); }
        catch (FormatException) { return false; }
        return secret.Length == 32;
    }
}
