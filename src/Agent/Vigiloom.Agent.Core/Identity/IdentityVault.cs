using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Vigiloom.Agent.Core.Identity;

/// <summary>A per-service-account identity store with an exclusive process lease.</summary>
public sealed class IdentityVault(string directory)
{
    private const int MaximumBytes = 16384;
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string directory = Path.IsPathFullyQualified(directory) && Path.GetPathRoot(directory) != directory
        ? Path.GetFullPath(directory) : throw new ArgumentException("A non-root absolute state directory is required.", nameof(directory));

    public string StateDirectory => directory;

    public Session Acquire()
    {
        var directoryGuard = PrepareDirectory(directory);
        try
        {
            var lockPath = Path.Combine(directory, "identity.lock");
            RejectLink(lockPath);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            var stream = new FileStream(lockPath, options);
            try
            {
                if (OperatingSystem.IsLinux()) ValidateUnixFile(lockPath);
                return new Session(directory, stream, directoryGuard);
            }
            catch { stream.Dispose(); throw; }
        }
        catch { directoryGuard?.Dispose(); throw; }
    }

    /// <summary>Allow one running settings synchronizer for an Agent state directory.</summary>
    public IAsyncDisposable AcquireSettingsOwnership()
    {
        var directoryGuard = PrepareDirectory(directory);
        try
        {
            var lockPath = Path.Combine(directory, "settings.lock");
            RejectLink(lockPath);
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            var stream = new FileStream(lockPath, options);
            try
            {
                if (OperatingSystem.IsLinux()) ValidateUnixFile(lockPath);
                return new SettingsOwnership(stream, directoryGuard);
            }
            catch { stream.Dispose(); throw; }
        }
        catch { directoryGuard?.Dispose(); throw; }
    }

    // Host transport operations are short, but heartbeat and batch I/O can briefly
    // contend for the same cross-process identity lease. Never hold this lease
    // while waiting for an HTTP response.
    internal async ValueTask<Session> AcquireForTransportAsync(CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return Acquire(); }
            catch (IOException) when (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * (attempt + 1)), token);
            }
        }
    }

    private static SafeFileHandle? PrepareDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return PrepareWindowsDirectory(path);
        else if (OperatingSystem.IsLinux()) { LinuxIdentityPaths.PrepareDirectory(path); return null; }
        else throw new PlatformNotSupportedException("The Agent identity vault supports Windows and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle PrepareWindowsDirectory(string path)
    {
        var current = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException("The service identity is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(current);
        foreach (var sid in new[] { current, system, administrators }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var info = security.CreateDirectory(path);
        var guard = WindowsIdentityPaths.OpenDirectory(path);
        try
        {
            for (DirectoryInfo? directory = info; directory is not null; directory = directory.Parent) RejectLink(directory.FullName);
            var existing = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!existing.AreAccessRulesProtected || existing.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != current)
                throw new UnauthorizedAccessException("The Agent identity directory has an unexpected owner or inherited permissions.");
            var allowed = new HashSet<SecurityIdentifier> { current, system, administrators };
            var rules = existing.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            if (rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference is SecurityIdentifier sid && !allowed.Contains(sid)) ||
                !rules.Any(rule => rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(current) &&
                    (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
                throw new UnauthorizedAccessException("The Agent identity directory grants unexpected access.");
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    [SupportedOSPlatform("linux")]
    private static void ValidateUnixFile(string path)
    {
        LinuxIdentityPaths.ValidateFile(path);
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Agent identity paths cannot be symbolic links.");
    }

    public sealed class Session : IAsyncDisposable
    {
        private readonly string directory;
        private readonly FileStream lease;
        private readonly SafeFileHandle? directoryGuard;
        internal Session(string directory, FileStream lease, SafeFileHandle? directoryGuard) { this.directory = directory; this.lease = lease; this.directoryGuard = directoryGuard; }

        internal string DirectoryPath => directory;

        public async Task<IdentityState?> ReadAsync(CancellationToken token)
        {
            var state = await ReadFileAsync<IdentityState>("identity.dat", token);
            if (state is not null && state.Version != 1) throw new InvalidDataException("Unsupported Agent identity state version.");
            return state;
        }

        internal Task<AgentAppliedSettings?> ReadSettingsAsync(CancellationToken token) =>
            ReadFileAsync<AgentAppliedSettings>("settings.dat", token);

        private async Task<T?> ReadFileAsync<T>(string fileName, CancellationToken token) where T : class
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) return null;
            RejectLink(path);
            if (OperatingSystem.IsLinux()) ValidateUnixFile(path);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (file.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid Agent identity state size.");
            var encoded = new byte[(int)file.Length];
            await file.ReadExactlyAsync(encoded, token);
            byte[]? plain = null;
            try
            {
                plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(encoded, null, DataProtectionScope.CurrentUser) : encoded;
                return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Invalid Agent state.");
            }
            finally { CryptographicOperations.ZeroMemory(encoded); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }

        public Task WriteAsync(IdentityState state, CancellationToken token) => WriteFileAsync("identity.dat", state, token);

        internal Task WriteSettingsAsync(AgentAppliedSettings settings, CancellationToken token) =>
            WriteFileAsync("settings.dat", settings, token);

        private async Task WriteFileAsync<T>(string fileName, T state, CancellationToken token)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(state);
            byte[]? encoded = null;
            var temp = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                encoded = OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser) : plain;
                if (encoded.Length > MaximumBytes) throw new InvalidDataException("Agent identity state is too large.");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
                await using (var file = new FileStream(temp, options))
                {
                    await file.WriteAsync(encoded, token);
                    await file.FlushAsync(token);
                    file.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temp, Path.Combine(directory, fileName), overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                CryptographicOperations.ZeroMemory(plain);
                if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync(); }
            finally { directoryGuard?.Dispose(); }
        }
    }

    private sealed class SettingsOwnership(FileStream lease, SafeFileHandle? directoryGuard) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await lease.DisposeAsync(); }
            finally { directoryGuard?.Dispose(); }
        }
    }
}
