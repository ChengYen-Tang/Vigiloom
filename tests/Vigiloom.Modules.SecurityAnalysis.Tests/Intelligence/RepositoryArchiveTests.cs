using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Repositories;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class RepositoryArchiveTests(TestContext context)
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestMethod]
    public async Task Read_AcceptsOnlyTheSelectedRepositoryRoot()
    {
        using var archive = Archive($"signature-base-{Commit}/iocs/hash-iocs.txt");
        var paths = new List<string>();
        await RepositoryArchive.ReadAsync(archive, "signature-base", Commit, (path, _, _) =>
        {
            paths.Add(path);
            return Task.CompletedTask;
        }, context.CancellationToken);
        CollectionAssert.AreEqual(new[] { "iocs/hash-iocs.txt" }, paths);

        archive.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryArchive.ReadAsync(archive, "sigma", Commit,
            (_, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    [TestMethod]
    public async Task Read_RejectsPathTraversal()
    {
        using var archive = Archive($"signature-base-{Commit}/iocs/../other.txt");
        await Assert.ThrowsAsync<InvalidDataException>(() => RepositoryArchive.ReadAsync(archive, "signature-base", Commit,
            (_, _, _) => Task.CompletedTask, context.CancellationToken));
    }

    private static MemoryStream Archive(string path)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry([new("comment", Commit)]));
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("example"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = content });
        }
        output.Position = 0;
        return output;
    }
}
