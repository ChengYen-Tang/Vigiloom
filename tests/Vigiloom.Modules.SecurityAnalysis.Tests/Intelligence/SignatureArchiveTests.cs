using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class SignatureArchiveTests(TestContext context)
{
    private const string Commit = "eb8c53cb08ed103d031fe9c98d1d23e94904839d";

    [TestMethod]
    public async Task Read_PreservesSourcesAndIndividualLicenseEvidence()
    {
        using var archive = Archive();
        var files = new Dictionary<string, SignatureFileKind>();
        var count = await SignatureArchive.ReadAsync(archive, Commit, 10, (path, kind, bytes, digest, _) =>
        {
            CollectionAssert.AreEqual(SHA256.HashData(bytes), digest);
            files.Add(path, kind);
            return Task.CompletedTask;
        }, context.CancellationToken);
        Assert.AreEqual(5, count);
        Assert.AreEqual(SignatureFileKind.Ioc, files["iocs/hash-iocs.txt"]);
        Assert.AreEqual(SignatureFileKind.Yara, files["yara/example.yar"]);
        Assert.AreEqual(SignatureFileKind.LicenseEvidence, files["yara/LICENSE"]);
        Assert.AreEqual(SignatureFileKind.LicenseEvidence, files["LICENSE"]);
        Assert.AreEqual(SignatureFileKind.LicenseEvidence, files["README.md"]);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Read_RejectsChangedOrMissingLicenseEvidence(bool changed, bool missing)
    {
        using var archive = Archive(changed, missing);
        await Assert.ThrowsAsync<SignatureLicenseException>(() => SignatureArchive.ReadAsync(archive, Commit, 10,
            (_, _, _, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    internal static MemoryStream Archive(bool changed = false, bool missing = false, string commit = Commit, bool lateInvalidEntry = false)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new("comment", commit)]));
            Write("LICENSE", changed ? new MemoryStream("changed"u8.ToArray()) : Fixture("LICENSE"));
            if (!missing) Write("README.md", Fixture("README.md"));
            Write("iocs/hash-iocs.txt", new MemoryStream("hash;description"u8.ToArray()));
            Write("yara/example.yar", new MemoryStream("rule example { meta: author = \"Example\" license = \"DRL-1.1\" condition: true }"u8.ToArray()));
            Write("yara/LICENSE", new MemoryStream("other license"u8.ToArray()));
            if (lateInvalidEntry)
            {
                for (var i = 0; i < 20; i++) Write($"yara/rule-{i}.yar", new MemoryStream("rule test { condition: true }"u8.ToArray()));
                Write("yara/../invalid.yar", new MemoryStream("invalid"u8.ToArray()));
            }

            void Write(string path, Stream bytes)
            {
                using (bytes) tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"signature-base-{commit}/{path}") { DataStream = bytes });
            }
        }
        output.Position = 0;
        return output;
    }

    private static Stream Fixture(string name) => typeof(SignatureArchiveTests).Assembly.GetManifestResourceStream(
        $"Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence.Fixtures.SignatureBase.{name}")!;
}
