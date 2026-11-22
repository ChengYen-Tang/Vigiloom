using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class CentralPackageAcquirerTests(TestContext context)
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DownloadsAssignedArchiveAndRejectsCorruption(bool corrupt)
    {
        var root = Path.Combine(context.TestRunDirectory!, $"central-package-{Guid.CreateVersion7():N}");
        try
        {
            var archive = Encoding.UTF8.GetBytes("package");
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.0.0", rid = "win-x64", size = archive.Length, sha256 = hash });
            var manifestHash = Convert.ToHexStringLower(SHA256.HashData(manifest));
            var handler = new FixtureHandler(manifest, corrupt ? Encoding.UTF8.GetBytes("corrupt") : archive);
            using var http = new HttpClient(handler);
            var source = new CentralPackageAcquirer(new Uri("https://example.test/"), root, TimeProvider.System, http);
            if (corrupt)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => source.AcquireAsync(manifestHash, "1.0.0", "win-x64", "vla_test", context.CancellationToken));
                Assert.IsEmpty(Directory.GetFiles(root, "*.pkg"));
            }
            else
            {
                await using var package = await source.AcquireAsync(manifestHash, "1.0.0", "win-x64", "vla_test", context.CancellationToken);
                using var reader = new StreamReader(package.Archive, Encoding.UTF8, leaveOpen: true);
                Assert.AreEqual("package", await reader.ReadToEndAsync(context.CancellationToken));
            }
            Assert.IsTrue(handler.AllAuthenticated);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class FixtureHandler(byte[] manifest, byte[] archive) : HttpMessageHandler
    {
        public bool AllAuthenticated { get; private set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            AllAuthenticated &= request.Headers.Authorization is { Scheme: "Bearer", Parameter: "vla_test" };
            var content = request.RequestUri!.AbsolutePath.Contains("/targets/", StringComparison.Ordinal) ? manifest : archive;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }
}
