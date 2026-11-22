using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Hosting;

/// <summary>Stores the last centrally validated credential in the installer-protected Updater state directory.</summary>
internal sealed class MachineCredentialStore(string directory)
{
    private readonly string path = Path.Combine(directory, "machine-credential.dat");
    private readonly SemaphoreSlim gate = new(1, 1);

    internal sealed record Credentials(string Current, string? Pending = null);

    public async Task WriteAsync(string credential, CancellationToken token)
    {
        if (!UpdateHandoff.TryGetAgentId(credential, out _)) throw new ArgumentException("Invalid Agent credential.", nameof(credential));
        await gate.WaitAsync(token);
        try
        {
            var existing = await ReadCoreAsync(token);
            var pending = existing?.Current == credential ? existing.Pending : null;
            await WriteCoreAsync(new(credential, pending), token);
        }
        finally { gate.Release(); }
    }

    public async Task StageAsync(string current, string pending, CancellationToken token)
    {
        if (!UpdateHandoff.TryGetAgentId(current, out var currentId) ||
            !UpdateHandoff.TryGetAgentId(pending, out var pendingId) || currentId != pendingId || current == pending)
            throw new ArgumentException("A matching current and pending Agent credential are required.");
        await gate.WaitAsync(token);
        try { await WriteCoreAsync(new(current, pending), token); }
        finally { gate.Release(); }
    }

    private async Task WriteCoreAsync(Credentials credentials, CancellationToken token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? encoded = null;
        var temporary = path + "." + Guid.CreateVersion7().ToString("N") + ".tmp";
        try
        {
            encoded = OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) : plain;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temporary, options))
            {
                await file.WriteAsync(encoded, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (encoded is not null && !ReferenceEquals(encoded, plain)) CryptographicOperations.ZeroMemory(encoded);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<string?> ReadAsync(CancellationToken token)
    {
        var values = await ReadCandidatesAsync(token);
        return values?.Current;
    }

    public async Task<Credentials?> ReadCandidatesAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await ReadCoreAsync(token); }
        finally { gate.Release(); }
    }

    private async Task<Credentials?> ReadCoreAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        var encoded = await File.ReadAllBytesAsync(path, token);
        byte[]? plain = null;
        try
        {
            plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(encoded, null, DataProtectionScope.CurrentUser) : encoded;
            var text = Encoding.UTF8.GetString(plain);
            var values = UpdateHandoff.TryGetAgentId(text, out _) ? new Credentials(text) : JsonSerializer.Deserialize<Credentials>(plain);
            if (values is null || !UpdateHandoff.TryGetAgentId(values.Current, out var currentId) ||
                values.Pending is not null && (!UpdateHandoff.TryGetAgentId(values.Pending, out var pendingId) || pendingId != currentId))
                throw new InvalidDataException("Invalid retained Agent credentials.");
            return values;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
            if (plain is not null && !ReferenceEquals(plain, encoded)) CryptographicOperations.ZeroMemory(plain);
        }
    }
}
