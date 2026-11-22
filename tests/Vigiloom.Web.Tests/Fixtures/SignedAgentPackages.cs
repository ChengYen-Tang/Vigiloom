using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vigiloom.Web.Tests.Fixtures;

// An in-memory GitHub Release source for exercising real Agent service updates.
internal sealed class SignedAgentPackages : IDisposable
{
    private readonly Dictionary<string, byte[]> archives = [];
    private readonly string rid;

    internal SignedAgentPackages(byte[] initialArchive, byte[] agentArchive, string runtimeIdentifier)
    {
        rid = runtimeIdentifier;
        using var failure = new MemoryStream();
        using (var zip = new ZipArchive(failure, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (rid == "linux-x64")
            {
                using var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent").Open());
                writer.Write("#!/bin/sh\nexit 1\n");
            }
            else
            {
                using var initial = new ZipArchive(new MemoryStream(initialArchive), ZipArchiveMode.Read);
                using var input = (initial.GetEntry("Vigiloom.Agent.exe") ?? throw new InvalidDataException("Agent apphost is missing.")).Open();
                using var output = zip.CreateEntry("Vigiloom.Agent.exe").Open();
                input.CopyTo(output);
            }
        }
        archives.Add("1.0.0", initialArchive);
        archives.Add("1.1.0", agentArchive);
        archives.Add("1.2.0", failure.ToArray());
    }

    internal HttpClient CreateClient() => new(new Handler(archives, rid)) { BaseAddress = new Uri("https://api.github.com/") };
    public void Dispose() { }

    private sealed class Handler(IReadOnlyDictionary<string, byte[]> archives, string rid) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var tag = parts.FirstOrDefault(part => part.StartsWith("agent-v", StringComparison.Ordinal));
            if (tag is null || !archives.TryGetValue(tag[7..], out var archive))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (request.RequestUri.Host == "github.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            var release = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = tag, draft = false, prerelease = false,
                assets = new[] { new { name = $"agent-{rid}.zip", size = archive.Length,
                    digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(archive)) } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(release) });
        }
    }
}
