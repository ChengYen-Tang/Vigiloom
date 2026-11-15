using System.IO.Compression;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class EpssLimitedStreamTests
{
    [TestMethod]
    public void Footer_RequiresCompleteMatchingTrailer()
    {
        var artifact = Compress("CVE-2025-1234,0.001,0.2\n");
        Verify(artifact);
        Assert.ThrowsExactly<InvalidDataException>(() => Verify(artifact[..^8]));
        Assert.ThrowsExactly<InvalidDataException>(() => Verify([.. artifact, 1, 2, 3]));
    }

    [TestMethod]
    public void ExpandedInput_IsBounded()
    {
        var artifact = Compress(new string('a', 100));
        Assert.ThrowsExactly<InvalidDataException>(() => Verify(artifact, 99));
        Verify(artifact, 100);
    }

    private static void Verify(byte[] artifact, long limit = 1000)
    {
        using var input = new MemoryStream(artifact);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var limited = new EpssLimitedStream(gzip, limit);
        limited.CopyTo(Stream.Null);
        limited.ValidateFooter(artifact);
    }

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}
