using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Vigiloom.UpdateSecurity.Tests;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Test-only publisher with real Agent archives and an intentionally failing version.</summary>
internal sealed class SignedAgentPackages : IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Dictionary<string, byte[]> archives = [];

    internal SignedAgentPackages(byte[] initialArchive, byte[] agentArchive, string runtimeIdentifier)
    {
        using var failure = new MemoryStream();
        using (var zip = new ZipArchive(failure, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (runtimeIdentifier == "linux-x64")
            {
                using var writer = new StreamWriter(zip.CreateEntry("Vigiloom.Agent").Open());
                writer.Write("#!/bin/sh\nexit 1\n");
            }
            else
            {
                // A real apphost without its managed DLL fails to start on Windows.
                using var initial = new ZipArchive(new MemoryStream(initialArchive), ZipArchiveMode.Read);
                using var input = (initial.GetEntry("Vigiloom.Agent.exe") ?? throw new InvalidDataException("Agent apphost is missing.")).Open();
                using var output = zip.CreateEntry("Vigiloom.Agent.exe").Open();
                input.CopyTo(output);
            }
        }

        var targets = new Dictionary<string, byte[]>();
        var packages = new List<object>();
        Add("1.0.0", initialArchive);
        Add("1.1.0", agentArchive);
        Add("1.2.0", failure.ToArray());
        var catalog = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, packages });
        Repository = new TestRepository(key, catalogue: catalog, additionalTargets: targets);
        Root = Repository.Root(1, key);
        return;

        void Add(string version, byte[] archive)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, version, rid = runtimeIdentifier, size = archive.Length, sha256 = hash,
                minimumUpdaterVersion = "1.0.0", minimumAgentVersion = (string?)null, maximumAgentVersion = (string?)null
            });
            var digest = Convert.ToHexStringLower(SHA256.HashData(manifest));
            archives.Add(hash, archive);
            targets.Add(digest + ".json", manifest);
            packages.Add(new { version, rid = runtimeIdentifier, size = archive.Length, sha256 = hash, manifestSha256 = digest, revoked = false });
        }
    }

    internal TestRepository Repository { get; }
    internal byte[] Root { get; }
    internal HttpClient CreateClient() => new(new Handler(Repository, archives));
    public void Dispose() => key.Dispose();

    private sealed class Handler(TestRepository repository, IReadOnlyDictionary<string, byte[]> archives) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var parts = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            byte[]? bytes = null;
            if (parts is ["packages", var hash]) archives.TryGetValue(hash, out bytes);
            else if (parts is ["targets", var target]) bytes = await repository.FetchTargetAsync(target, token);
            else if (parts is ["metadata", var name])
            {
                var fields = name.Split('.');
                var version = fields.Length == 3 && int.TryParse(fields[0], out var parsed) ? parsed : (int?)null;
                bytes = await repository.FetchMetadataAsync(fields[version is null ? 0 : 1], version, token);
            }
            return bytes is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}
