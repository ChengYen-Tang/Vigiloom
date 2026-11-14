using System.Text;
using System.IO.Compression;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class SigmaRuleValidatorTests(TestContext context)
{
    [TestMethod]
    public void Validate_PreservesMetadataAndDoesNotInterpretDetection()
    {
        var yaml = """
            title: Suspicious process
            id: 929a690e-bef0-4204-a928-ef5e620d6fcc
            status: test
            author: Example Author
            logsource:
              product: windows
            detection:
              selection:
                Image|endswith:
                  - '\\powershell.exe'
              condition: selection
            """;
        var result = SigmaRuleValidator.Validate(Encoding.UTF8.GetBytes(yaml));
        Assert.AreEqual("Suspicious process", result.Title);
        Assert.AreEqual("929a690e-bef0-4204-a928-ef5e620d6fcc", result.Id);
        Assert.AreEqual("Example Author", result.Author);
        Assert.AreEqual("test", result.Status);
        Assert.IsNull(result.License);
        Assert.AreEqual("windows", result.Product);
    }

    [TestMethod]
    [DataRow("title: x\ntitle: y\nlogsource: {}\ndetection: {}")]
    [DataRow("title: x\nlogsource: {}\ndetection: {a: 1, a: 2}")]
    [DataRow("title: &name x\nlogsource: {}\ndetection: {condition: *name}")]
    [DataRow("title: !custom x\nlogsource: {}\ndetection: {}")]
    [DataRow("title: x\nlogsource: []\ndetection: {}")]
    [DataRow("title: x\nlicense: null\nlogsource: {}\ndetection: {}")]
    [DataRow("title: x\nlogsource: {}\ndetection: {}\n---\ntitle: y")]
    public void Validate_RejectsAmbiguousOrUnsafeYaml(string yaml) =>
        Assert.Throws<InvalidDataException>(() => SigmaRuleValidator.Validate(Encoding.UTF8.GetBytes(yaml)));

    [TestMethod]
    public void Validate_RejectsOversizedAndInvalidUtf8()
    {
        Assert.Throws<InvalidDataException>(() => SigmaRuleValidator.Validate(new byte[SigmaRuleValidator.MaximumRuleBytes + 1]));
        Assert.Throws<InvalidDataException>(() => SigmaRuleValidator.Validate([0xff, 0xfe]));
    }

    [TestMethod]
    [DataRow((byte)'x')]
    [DataRow((byte)'g')]
    [DataRow((byte)'L')]
    [DataRow((byte)'K')]
    public async Task Archive_RejectsHugeDeclaredMetadataBeforeReadingItsBody(byte type)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(Convert.ToString(1_073_741_824, 8).PadLeft(11, '0'), header.AsSpan(124, 11));
        header[156] = type;
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true)) await gzip.WriteAsync(header, context.CancellationToken);
        Assert.IsLessThan(1024, archive.Length);
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => SigmaArchive.ReadAsync(archive, new string('a', 40), 50_000,
            (_, _, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    [TestMethod]
    public async Task Archive_RejectsDigitsAfterNullInTarSize()
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes("000000\0" + "00100", header.AsSpan(124, 12));
        header[156] = (byte)'0';
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(header, context.CancellationToken);
            await gzip.WriteAsync(new byte[1536], context.CancellationToken);
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryTarPreflight.ValidateAsync(archive, context.CancellationToken));
    }

    [TestMethod]
    public async Task Archive_RejectsPaxSizeOverride()
    {
        var body = Encoding.ASCII.GetBytes("15 size=999999\n");
        var header = new byte[512];
        Encoding.ASCII.GetBytes(Convert.ToString(body.Length, 8).PadLeft(11, '0'), header.AsSpan(124, 11));
        header[156] = (byte)'x';
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gzip.WriteAsync(header, context.CancellationToken);
            await gzip.WriteAsync(body, context.CancellationToken);
            await gzip.WriteAsync(new byte[512 - body.Length + 1024], context.CancellationToken);
        }
        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryTarPreflight.ValidateAsync(archive, context.CancellationToken));
    }
}
