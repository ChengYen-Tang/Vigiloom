using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class EpssCsvTests
{
    public TestContext TestContext { get; set; } = null!;
    private const string Header = "#model_version:v2026.06.15,score_date:2026-11-14T12:00:22Z\ncve,epss,percentile\n";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Read_StreamsMetadataAndExactDecimalScores()
    {
        var scores = new List<EpssScore>();
        EpssMetadata? metadata = null;
        using var stream = Input(Header + "CVE-1999-0001,0.04753,0.92024\nCVE-2025-12345,1.000000000,6e-05\n");
        var count = await EpssCsv.ReadAsync(stream, Now, 10,
            (value, _) => { metadata = value; return ValueTask.CompletedTask; },
            (value, _) => { scores.Add(value); return ValueTask.CompletedTask; }, TestContext.CancellationToken);
        Assert.AreEqual(2, count);
        Assert.AreEqual("v2026.06.15", metadata!.ModelVersion);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 14, 12, 0, 22, TimeSpan.Zero), metadata.ScoredAt);
        Assert.AreEqual(new EpssScore("CVE-1999-0001", 0.04753m, 0.92024m), scores[0]);
        Assert.AreEqual(new EpssScore("CVE-2025-12345", 1m, 0.00006m), scores[1]);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [DataRow("CVE-2025-1234,-0.1,0.2")]
    [DataRow("CVE-2025-1234,1.1,0.2")]
    [DataRow("CVE-2025-1234,1e-100,0.2")]
    [DataRow("CVE-2025-1234,1e-10,0.2")]
    [DataRow("CVE-2025-1234,0.1234567891,0.2")]
    [DataRow("CVE-2025-1234,1.00000000000000000000000000001,0.2")]
    [DataRow("CVE-2025-1234,0.10000000000000000000000000001,0.2")]
    [DataRow("cve-2025-1234,0.1,0.2")]
    [DataRow("CVE-2025-1234,0.1,0.2,extra")]
    [DataRow("CVE-2025-1234,0.1")]
    [DataRow("\"CVE-2025-1234,0.1,0.2")]
    public async Task Read_RejectsInvalidOrMalformedRows(string row)
    {
        using var stream = Input(Header + row + "\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(stream));
    }

    [TestMethod]
    public async Task Read_RejectsEmptyStaleFormatAndExcessRecords()
    {
        using var empty = Input(Header);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(empty));
        using var future = Input(Header.Replace("2026-11-14", "2030-11-14") + "CVE-2025-1234,0.1,0.2\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(future));
        using var noDate = Input(Header.Replace("2026-11-14T12:00:22Z", "12:00") + "CVE-2025-1234,0.1,0.2\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(noDate));
        using var noZone = Input(Header.Replace("2026-11-14T12:00:22Z", "2026-11-14T12:00:22") + "CVE-2025-1234,0.1,0.2\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(noZone));
        using var missingYear = Input(Header.Replace("2026-11-14T12:00:22Z", "11-14T12:00:22Z") + "CVE-2025-1234,0.1,0.2\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(missingYear));
        using var nonUtc = Input(Header.Replace("2026-11-14T12:00:22Z", "2026-11-14T12:00:22+08:00") + "CVE-2025-1234,0.1,0.2\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(nonUtc));
        using var utcOffset = Input(Header.Replace("2026-11-14T12:00:22Z", "2026-11-14T12:00:22+0000") + "CVE-2025-1234,6e-05,0.2\n");
        Assert.AreEqual(1, await Read(utcOffset));
        using var excess = Input(Header + "CVE-2025-1234,0.1,0.2\nCVE-2025-1235,0.2,0.3\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(excess, 1));
        using var huge = Input(Header + "CVE-2025-1234," + new string('0', 10_000) + ",0.1\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Read(huge));
    }

    private Task<int> Read(Stream stream, int max = 10) => EpssCsv.ReadAsync(stream, Now, max,
        (_, _) => ValueTask.CompletedTask, (_, _) => ValueTask.CompletedTask, TestContext.CancellationToken);
    private static MemoryStream Input(string content) => new(Encoding.UTF8.GetBytes(content));
}
