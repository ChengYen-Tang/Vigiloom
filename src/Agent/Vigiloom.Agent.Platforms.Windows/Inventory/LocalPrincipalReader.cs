using System.DirectoryServices.AccountManagement;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

public sealed record WindowsLocalPrincipal(string Sid, string Name, IReadOnlyList<string>? MemberSids);

/// <summary>Enumerates local SAM principals and direct group member SIDs, without credentials.</summary>
[SupportedOSPlatform("windows")]
public static class LocalPrincipalReader
{
    public static InventoryReadResult<WindowsLocalPrincipal> ReadAccounts(CancellationToken token) => Read(false, token);
    public static InventoryReadResult<WindowsLocalPrincipal> ReadGroups(CancellationToken token) => Read(true, token);

    private static InventoryReadResult<WindowsLocalPrincipal> Read(bool groups, CancellationToken token)
    {
        var items = new List<WindowsLocalPrincipal>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        var memberCount = 0;
        var limitReached = false;
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using Principal filter = groups ? new GroupPrincipal(context) : new UserPrincipal(context);
            using var searcher = new PrincipalSearcher(filter);
            using var results = searcher.FindAll();
            foreach (var principal in results)
            {
                using (principal)
                {
                    token.ThrowIfCancellationRequested();
                    if (items.Count >= InventoryChunkCodec.MaximumSnapshotItems) { partial = true; break; }
                    var sid = principal.Sid?.Value;
                    var name = principal.SamAccountName ?? principal.Name;
                    if (!Valid(sid, InventoryChunkCodec.MaximumMemberLength) || !Valid(name, InventoryChunkCodec.MaximumEntryTextLength) || !keys.Add(sid!))
                    {
                        partial = true;
                        continue;
                    }
                    IReadOnlyList<string>? members = null;
                    if (groups && principal is GroupPrincipal)
                    {
                        try
                        {
                            if (!SameGroup(context, name!, sid!)) { partial = true; members = []; }
                            else
                            {
                                var read = LocalGroupMembers.Read(name!, InventoryChunkCodec.MaximumSnapshotMembers - memberCount, token);
                                if (!SameGroup(context, name!, sid!)) { partial = true; members = []; }
                                else
                                {
                                    members = read.Members;
                                    memberCount += read.Members.Count;
                                    partial |= read.Partial;
                                    limitReached = read.LimitReached;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is PrincipalException or COMException or UnauthorizedAccessException or ArgumentException)
                        {
                            partial = true;
                            members = [];
                        }
                    }
                    else if (groups) { partial = true; continue; }
                    items.Add(new(sid!, name!, members));
                    if (limitReached) break;
                }
            }
            return new(partial ? InventoryReadStatus.Partial : InventoryReadStatus.Complete, items);
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is PrincipalException or COMException) { return new(InventoryReadStatus.Error, []); }
    }

    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);

    private static bool SameGroup(PrincipalContext context, string name, string sid)
    {
        using var group = GroupPrincipal.FindByIdentity(context, IdentityType.SamAccountName, name);
        return string.Equals(group?.Sid?.Value, sid, StringComparison.OrdinalIgnoreCase);
    }
}
