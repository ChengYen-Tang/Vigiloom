using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class UbuntuOsvDocumentTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Create_PreservesBytesAndDeduplicatesOnlyIdenticalPackageScopes()
    {
        var raw = Encoding.UTF8.GetBytes("""
            {"id":"UBUNTU-CVE-2025-0012","modified":"2025-01-01T00:00:00Z","affected":[
              {"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"openssl"}},
              {"package":{"ecosystem":"Ubuntu:24.04:LTS","name":"openssl"}},
              {"package":{"ecosystem":"Ubuntu:22.04","name":"openssl"}}]}
            """);
        var record = UbuntuOsvReader.Read(raw, 1024);
        var document = await UbuntuOsvDocument.CreateAsync(record, raw, TestContext.CancellationToken);

        CollectionAssert.AreEqual(SHA256.HashData(raw), document.Digest);
        Assert.AreEqual(raw.Length, document.OriginalLength);
        Assert.HasCount(2, document.Scopes);
        CollectionAssert.Contains(document.Scopes, new UbuntuPackageScope("Ubuntu:24.04:LTS", "openssl"));
        CollectionAssert.Contains(document.Scopes, new UbuntuPackageScope("Ubuntu:22.04", "openssl"));
        using var input = new BrotliStream(new MemoryStream(document.Compressed), CompressionMode.Decompress);
        using var restored = new MemoryStream();
        await input.CopyToAsync(restored, TestContext.CancellationToken);
        CollectionAssert.AreEqual(raw, restored.ToArray());
    }
}
