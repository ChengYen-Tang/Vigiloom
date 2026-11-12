using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Win32;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Agent.Platforms.Windows.Inventory;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Platforms.Windows.Integrity;

/// <summary>Reads a bounded registry scope; instability and denied subkeys never imply deletions.</summary>
[SupportedOSPlatform("windows")]
public static class RegistryScopeReader
{
    public static IntegrityReadResult Read(IntegrityScope scope, CancellationToken token)
    {
        if (!scope.IsValid() || scope.Kind != IntegrityScopeKind.Registry) throw new ArgumentException("Invalid registry scope.", nameof(scope));
        if (scope.RegistryView == IntegrityRegistryView.Registry64 && !Environment.Is64BitOperatingSystem)
            return new(IntegrityScanOutcome.Unsupported, [], false);
        var parts = scope.Root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return new(IntegrityScanOutcome.Unsupported, [], false);
        if (parts[0].Equals("HKU", StringComparison.OrdinalIgnoreCase) &&
            (!parts[1].StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) || parts[1].EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
            return new(IntegrityScanOutcome.Unsupported, [], false);
        var hive = parts[0].Equals("HKLM", StringComparison.OrdinalIgnoreCase) ? RegistryHive.LocalMachine : RegistryHive.Users;
        // Sensitive hives require an explicit security product design; never read them by default.
        if (hive == RegistryHive.LocalMachine && (parts[1].Equals("SAM", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("SECURITY", StringComparison.OrdinalIgnoreCase)))
            return new(IntegrityScanOutcome.Unsupported, [], false);
        var view = scope.RegistryView == IntegrityRegistryView.Registry64 ? RegistryView.Registry64 : RegistryView.Registry32;
        var target = string.Join('\\', parts.Skip(1));
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude("**/*");
        foreach (var exclusion in scope.Exclusions) matcher.AddExclude(exclusion);
        var entries = new List<IntegrityItem>();
        var stack = new Stack<(string Path, string[] Relative, int Depth)>();
        stack.Push((target, [], 0));
        long bytes = 0;
        IntegrityPartialReason reasons = 0;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (path, relative, depth) = stack.Pop();
                try
                {
                    using var key = baseKey.OpenSubKey(path, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey);
                    if (key is null) { reasons |= IntegrityPartialReason.ChangedDuringScan; continue; }
                    if (!RegistryRead.IntegrityStamp(key.Handle, out var subkeyCount, out var valueCount, out var lastWrite))
                    { reasons |= IntegrityPartialReason.Other; continue; }
                    var security = key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                    var descriptor = security.GetSecurityDescriptorBinaryForm();
                    var securityDigest = Convert.ToHexStringLower(SHA256.HashData(descriptor));
                    var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
                    if (owner is null) reasons |= IntegrityPartialReason.Other;
                    if (!Add(new(IntegrityKey.Create(IntegrityItemKind.RegistryKey, relative), IntegrityItemKind.RegistryKey,
                        SecurityDigest: securityDigest, Owner: owner))) break;
                    for (uint valueIndex = 0; valueIndex < valueCount; valueIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (valueIndex >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; break; }
                        var nameStatus = RegistryRead.ValueName(key.Handle, valueIndex, out var value);
                        if (nameStatus != 0 || value is null)
                        {
                            reasons |= nameStatus == 234 ? IntegrityPartialReason.LimitReached : IntegrityPartialReason.ChangedDuringScan;
                            break;
                        }
                        var valuePath = relative.Append(value).ToArray();
                        if (Excluded(valuePath)) continue;
                        var maximum = (int)Math.Min(16L * 1024 * 1024, Math.Min(scope.MaximumFileBytes, Math.Max(0, scope.MaximumScanBytes - bytes)));
                        var result = RegistryValueReader.Read(key.Handle, value, maximum);
                        if (result.Status != RegistryValueReadStatus.Success)
                        {
                            reasons |= result.Status switch
                            {
                                RegistryValueReadStatus.PermissionDenied => IntegrityPartialReason.AccessDenied,
                                RegistryValueReadStatus.TooLarge => IntegrityPartialReason.LimitReached,
                                RegistryValueReadStatus.Changed or RegistryValueReadStatus.Missing => IntegrityPartialReason.ChangedDuringScan,
                                _ => IntegrityPartialReason.Other
                            };
                            continue;
                        }
                        bytes += result.Bytes!.Value;
                        if (!Add(new(IntegrityKey.Create(IntegrityItemKind.RegistryValue, valuePath), IntegrityItemKind.RegistryValue, result.Digest))) break;
                    }
                    if (entries.Count >= scope.MaximumEntries && (subkeyCount > 0 || stack.Count > 0))
                    { reasons |= IntegrityPartialReason.LimitReached; break; }
                    for (uint subkeyIndex = 0; subkeyIndex < subkeyCount; subkeyIndex++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (subkeyIndex >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; break; }
                        var nameStatus = RegistryRead.Name(key.Handle, subkeyIndex, out var subkey);
                        if (nameStatus != 0 || subkey is null)
                        {
                            reasons |= nameStatus == 234 ? IntegrityPartialReason.LimitReached : IntegrityPartialReason.ChangedDuringScan;
                            break;
                        }
                        var child = relative.Append(subkey).ToArray();
                        if (Excluded(child)) continue;
                        if (depth >= scope.MaximumDepth) { reasons |= IntegrityPartialReason.LimitReached; continue; }
                        if (stack.Count + entries.Count >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; break; }
                        stack.Push(($"{path}\\{subkey}", child, depth + 1));
                    }
                    if (!RegistryRead.IntegrityStamp(key.Handle, out var afterSubkeys, out var afterValues, out var afterWrite) ||
                        afterSubkeys != subkeyCount || afterValues != valueCount || afterWrite != lastWrite)
                        reasons |= IntegrityPartialReason.ChangedDuringScan;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                { reasons |= IntegrityPartialReason.AccessDenied; }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
                { reasons |= IntegrityPartialReason.Other; }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        { return new(IntegrityScanOutcome.PermissionDenied, [], false); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        { return new(IntegrityScanOutcome.Error, [], false); }
        return new(reasons == 0 ? IntegrityScanOutcome.Complete : IntegrityScanOutcome.Partial, entries, false,
            reasons == 0 ? null : reasons);

        bool Excluded(string[] components)
        {
            if (components.Length == 0) return false;
            var path = string.Join('/', components);
            return !matcher.Match(path).HasMatches;
        }
        bool Add(IntegrityItem item)
        {
            if (entries.Count >= scope.MaximumEntries) { reasons |= IntegrityPartialReason.LimitReached; return false; }
            entries.Add(item);
            return true;
        }
    }
}
