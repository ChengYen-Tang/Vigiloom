using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Packages;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class PackageStagerTests(TestContext context)
{
    [TestMethod]
    public async Task InsufficientSpaceDoesNotChangeRunningVersion()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-space-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            var running = Path.Combine(root, "current.txt");
            await File.WriteAllTextAsync(running, "old", context.CancellationToken);
            using var archive = OversizedArchive();
            var manifest = Manifest(archive, "win-x64");
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => PackageStager.PrepareAsync(archive, manifest,
                Target(manifest), root, "win-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken));
            Assert.Contains("not enough space", error.Message);
            Assert.AreEqual("old", await File.ReadAllTextAsync(running, context.CancellationToken));
            Assert.IsEmpty(Directory.EnumerateDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // A tiny ZIP64 fixture declares an enormous extracted size, without filling the test machine's disk.
    private static MemoryStream OversizedArchive()
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var name = Encoding.UTF8.GetBytes("Vigiloom.Agent.exe");
        const uint crc = 0x8cdc1683; // CRC-32 of "x".
        writer.Write(0x04034b50u); writer.Write((ushort)45); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(0u); writer.Write(crc); writer.Write(1u); writer.Write(1u);
        writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write((byte)'x');
        var centralOffset = checked((uint)stream.Position);
        writer.Write(0x02014b50u); writer.Write((ushort)45); writer.Write((ushort)45); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(0u); writer.Write(crc); writer.Write(1u); writer.Write(uint.MaxValue);
        writer.Write((ushort)name.Length); writer.Write((ushort)12); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write((ushort)0); writer.Write(0u); writer.Write(0u); writer.Write(name);
        writer.Write((ushort)1); writer.Write((ushort)8); writer.Write(long.MaxValue - 1);
        var centralLength = checked((uint)stream.Position - centralOffset);
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(centralLength); writer.Write(centralOffset); writer.Write((ushort)0);
        stream.Position = 0;
        return stream;
    }

    [TestMethod]
    public async Task PreparesVerifiedPackageWithoutChangingRunningFiles()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var running = Path.Combine(root, "current.txt");
            await File.WriteAllTextAsync(running, "old", context.CancellationToken);
            using var archive = Archive(("Vigiloom.Agent.exe", "new"), ("appsettings.json", "{}"));
            var manifest = Manifest(archive, "win-x64");
            var prepared = await PackageStager.PrepareAsync(archive, manifest, Target(manifest), root, "win-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken);
            Assert.AreEqual("new", await File.ReadAllTextAsync(Path.Combine(prepared, "Vigiloom.Agent.exe"), context.CancellationToken));
            Assert.AreEqual("old", await File.ReadAllTextAsync(running, context.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PreparesLinuxExecutable()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var archive = Archive(("Vigiloom.Agent", "binary"), ("runtimes/native.dll", "library"), ("tools/createdump", "tool"));
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
                zip.GetEntry("tools/createdump")!.ExternalAttributes = unchecked((int)0x81ed0000);
            archive.Position = 0;
            var manifest = Manifest(archive, "linux-x64");
            var prepared = await PackageStager.PrepareAsync(archive, manifest, Target(manifest), root, "linux-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken);
            var executable = Path.Combine(prepared, "Vigiloom.Agent");
            Assert.IsTrue(File.Exists(executable));
            if (OperatingSystem.IsLinux())
            {
                const UnixFileMode readOnlyForOthers = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                const UnixFileMode executableMode = readOnlyForOthers | UnixFileMode.UserExecute |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                Assert.AreEqual(executableMode, File.GetUnixFileMode(executable));
                Assert.AreEqual(readOnlyForOthers, File.GetUnixFileMode(Path.Combine(prepared, "runtimes", "native.dll")));
                Assert.AreEqual(executableMode, File.GetUnixFileMode(Path.Combine(prepared, "tools", "createdump")));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RejectsCorruptIncompatibleAndEscapingArchives()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var archive = Archive(("Vigiloom.Agent.exe", "new"));
            var manifest = Manifest(archive, "win-x64");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PackageStager.PrepareAsync(archive, manifest, Target(manifest), root,
                "linux-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken));
            var bytes = archive.ToArray();
            bytes[^1] ^= 1;
            using var corrupt = new MemoryStream(bytes);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PackageStager.PrepareAsync(corrupt, manifest, Target(manifest), root,
                "win-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken));
            using var escape = Archive(("../outside.txt", "outside"), ("Vigiloom.Agent.exe", "new"));
            var escapeManifest = Manifest(escape, "win-x64");
            await Assert.ThrowsExactlyAsync<IOException>(() => PackageStager.PrepareAsync(escape, escapeManifest, Target(escapeManifest), root,
                "win-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken));
            Assert.IsFalse(File.Exists(Path.Combine(root, "staging", "outside.txt")));
            Assert.IsEmpty(Directory.EnumerateDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RejectsManifestThatDoesNotMatchAssignedVersion()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var archive = Archive(("Vigiloom.Agent.exe", "new"));
            var manifest = Manifest(archive, "win-x64");
            var target = Target(manifest);
            foreach (var mismatch in new[] { target with { PackageVersion = "2.0.0" },
                target with { RuntimeIdentifier = "win-x86" }, target with { ManifestSha256 = new string('a', 64) } })
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PackageStager.PrepareAsync(archive, manifest, mismatch, root,
                    "win-x64", "1.0.0", "1.0.0", TimeProvider.System, context.CancellationToken));
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RemovesStagedFilesWhenManifestExpiresDuringPreparation()
    {
        var root = Path.Combine(context.TestRunDirectory!, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var archive = Archive(("Vigiloom.Agent.exe", "new"));
            var manifest = Manifest(archive, "win-x64");
            var clock = new ExpiringClock(manifest.Expires.AddSeconds(-1), manifest.Expires);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PackageStager.PrepareAsync(archive, manifest, Target(manifest), root,
                "win-x64", "1.0.0", "1.0.0", clock, context.CancellationToken));
            Assert.IsEmpty(Directory.EnumerateDirectories(Path.Combine(root, "staging")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ExpiringClock(DateTimeOffset before, DateTimeOffset after) : TimeProvider
    {
        private int reads;
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref reads) == 1 ? before : after;
    }

    private static AgentVersionTarget Target(PackageManifest manifest) => new(Guid.CreateVersion7(), Guid.CreateVersion7(),
        manifest.Version, manifest.RuntimeIdentifier, manifest.ManifestSha256, Convert.ToBase64String(new byte[8]));

    private static MemoryStream Archive(params (string Name, string Content)[] files)
    {
        var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        result.Position = 0;
        return result;
    }

    private static PackageManifest Manifest(MemoryStream archive, string rid)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive.ToArray()));
        var json = $$"""{"schema":1,"version":"1.0.0","rid":"{{rid}}","size":{{archive.Length}},"sha256":"{{hash}}","minimumUpdaterVersion":"1.0.0","minimumAgentVersion":null,"maximumAgentVersion":null}""";
        return PackageManifest.Parse(new VerifiedManifest(Encoding.UTF8.GetBytes(json), DateTimeOffset.UtcNow.AddMinutes(5)), DateTimeOffset.UtcNow);
    }
}
