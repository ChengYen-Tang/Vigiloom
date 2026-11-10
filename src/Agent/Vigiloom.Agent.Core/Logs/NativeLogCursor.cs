namespace Vigiloom.Agent.Core.Logs;

/// <summary>Distinguishes a native cursor from a durable request to restart after a reported gap.</summary>
public static class NativeLogCursor
{
    public const string Restart = "r";
    private const int MaximumLength = 4096;

    public static string Encode(string nativeCursor)
    {
        if (string.IsNullOrEmpty(nativeCursor) || nativeCursor.Length >= MaximumLength)
            throw new InvalidDataException("Native source cursor is unavailable or too long.");
        return "p" + nativeCursor;
    }

    public static string? Decode(string? cursor)
    {
        if (cursor is null or Restart) return null;
        if (cursor.Length is < 2 or > MaximumLength || cursor[0] != 'p')
            throw new InvalidDataException("Invalid native source cursor.");
        return cursor[1..];
    }
}
