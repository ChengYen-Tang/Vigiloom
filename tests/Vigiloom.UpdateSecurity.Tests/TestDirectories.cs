namespace Vigiloom.UpdateSecurity.Tests;

internal static class TestDirectories
{
    private static readonly string[] Prefixes =
    ["vigiloom-cache-test-", "vigiloom-trust-test-", "vigiloom-package-test-", "vigiloom-interop-test-", "vigiloom-manifest-test-"];

    public static void Delete(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = Path.GetFileName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison) ||
            !Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _)))
            throw new InvalidOperationException("Refusing to remove a directory outside this test fixture's temporary paths.");
        if (!Directory.Exists(full)) return;
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to remove a linked temporary directory.");
        Directory.Delete(full, recursive: true);
    }
}
