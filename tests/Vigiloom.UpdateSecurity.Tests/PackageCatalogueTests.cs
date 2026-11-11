using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class PackageCatalogueTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Parse_AcceptsAuthenticatedCompatiblePackageMetadata()
    {
        var catalog = Parse($$"""{"schema":1,"packages":[{"version":"1.2.3","rid":"win-x64","size":1024,"sha256":"{{Hash}}","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":false}]}""");
        Assert.HasCount(1, catalog.Packages);
        Assert.AreEqual(Hash, catalog.Packages[0].Sha256);
        Assert.AreEqual("win-x64", catalog.Packages[0].Rid);
    }

    [TestMethod]
    public void Parse_RejectsUnsupportedDataAndExpiredMetadata()
    {
        var valid = $$"""{"schema":1,"packages":[{"version":"1.2.3","rid":"win-x64","size":1024,"sha256":"{{Hash}}","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":false}]}""";
        Assert.ThrowsExactly<InvalidDataException>(() => PackageCatalogue.Parse(new VerifiedCatalogue(Encoding.UTF8.GetBytes(valid), Now), Now));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace("win-x64", "linux-x86", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace("1.2.3", "1.2.3+other", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace(Hash, Hash.ToUpperInvariant(), StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace("\"size\":1024", "\"size\":0", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace(",\"revoked\":false", "", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(valid.Replace("\"revoked\":false", "\"revoked\":false,\"url\":\"https://example.test/\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Parse_RejectsDuplicateIdentity()
    {
        var entry = $$"""{"version":"1.2.3","rid":"win-x64","size":1024,"sha256":"{{Hash}}","manifestSha256":"0000000000000000000000000000000000000000000000000000000000000000","revoked":false}""";
        Assert.ThrowsExactly<InvalidDataException>(() => Parse($$"""{"schema":1,"packages":[{{entry}},{{entry}}]}"""));
    }

    private static PackageCatalogue Parse(string json) =>
        PackageCatalogue.Parse(new VerifiedCatalogue(Encoding.UTF8.GetBytes(json), Now.AddHours(1)), Now);
}
