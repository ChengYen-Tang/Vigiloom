using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class KevCatalogValidatorTests
{
    private const string Entry = """
        {"cveID":"CVE-2026-1234","vendorProject":"Vendor","product":"Product",
         "vulnerabilityName":"Example","dateAdded":"2026-11-01","shortDescription":"Description",
         "requiredAction":"Update","dueDate":"2026-12-01"}
        """;

    private static byte[] Catalog(string records, int count) => Encoding.UTF8.GetBytes($$"""
        {"catalogVersion":"2026.11.01","dateReleased":"2026-11-01T12:30:00Z",
         "count":{{count}},"vulnerabilities":[{{records}}]}
        """);

    [TestMethod]
    public void Validate_ValidCatalog_ReturnsSourceMetadata()
    {
        var metadata = KevCatalogValidator.Validate(Catalog(Entry, 1), 4096, 10);

        Assert.AreEqual("2026.11.01", metadata.Version);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero), metadata.ReleasedAt);
        Assert.AreEqual(1, metadata.RecordCount);
    }

    [TestMethod]
    public void Validate_DuplicateCveOrCountMismatch_RejectsSnapshot()
    {
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog($"{Entry},{Entry}", 2), 4096, 10));
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog(Entry, 0), 4096, 10));
    }

    [TestMethod]
    public void Validate_MissingRequiredPropertyOrInvalidDate_RejectsSnapshot()
    {
        var missing = Entry.Replace("\"requiredAction\":\"Update\",", "", StringComparison.Ordinal);
        var invalidDate = Entry.Replace("2026-12-01", "2026-02-30", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog(missing, 1), 4096, 10));
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog(invalidDate, 1), 4096, 10));
    }

    [TestMethod]
    public void Validate_TooLargeOrTruncated_RejectsSnapshot()
    {
        var bytes = Catalog(Entry, 1);
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(bytes, bytes.Length - 1, 10));
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(bytes.AsMemory(0, bytes.Length - 1), 4096, 10));
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog($"{Entry},{Entry}", 2), 4096, 1));
    }

    [TestMethod]
    public void Validate_MalformedInput_RejectsSnapshot()
    {
        var valid = Encoding.UTF8.GetString(Catalog(Entry, 1));
        var duplicate = valid.Replace("\"count\":1", "\"count\":1,\"count\":1", StringComparison.Ordinal);
        var missingCve = valid.Replace("\"cveID\":\"CVE-2026-1234\",", "", StringComparison.Ordinal);
        var invalidCve = valid.Replace("CVE-2026-1234", "not-a-cve", StringComparison.Ordinal);
        var invalidRelease = valid.Replace("2026-11-01T12:30:00Z", "2026-11-01 12:30:00", StringComparison.Ordinal);
        var invalidUtf8 = Catalog(Entry, 1);
        invalidUtf8[Array.IndexOf(invalidUtf8, (byte)'V')] = 0xff;

        foreach (var input in new[] { duplicate, missingCve, invalidCve, invalidRelease })
            Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Encoding.UTF8.GetBytes(input), 4096, 10));
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(invalidUtf8, 4096, 10));
    }

    [TestMethod]
    public void Validate_NestedDuplicateAndUnknownInvalidUtf8_RejectsSnapshot()
    {
        var duplicate = Entry.Replace("\"product\":\"Product\"", "\"product\":\"Product\",\"product\":\"Other\"", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Catalog(duplicate, 1), 4096, 10));
        var bytes = Catalog(Entry.Replace("\"product\":\"Product\"", "\"product\":\"Product\",\"extra\":\"marker\"", StringComparison.Ordinal), 1);
        bytes[Encoding.UTF8.GetString(bytes).IndexOf("marker", StringComparison.Ordinal)] = 0xff;
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(bytes, 4096, 10));
    }

    [TestMethod]
    public void Validate_OffsetAndExactLimits_AreHandledCorrectly()
    {
        var input = Encoding.UTF8.GetString(Catalog(Entry, 1)).Replace("2026-11-01T12:30:00Z", "2026-11-01T20:30:00+08:00", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(input);
        var metadata = KevCatalogValidator.Validate(bytes, bytes.Length, 1);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero), metadata.ReleasedAt);
        var invalid = input.Replace("2026-11-01T20:30:00+08:00", "2026-02-30T20:30:00+08:00", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => KevCatalogValidator.Validate(Encoding.UTF8.GetBytes(invalid), 4096, 1));
    }
}
