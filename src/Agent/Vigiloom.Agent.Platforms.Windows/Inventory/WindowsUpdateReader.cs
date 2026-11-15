using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.CSharp.RuntimeBinder;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Platforms.Windows.Inventory;

/// <summary>A WUA cache observation, not proof that the cache contains every relevant update.</summary>
public sealed record WindowsUpdateObservation(Guid UpdateId, int Revision, string Title, bool Installed,
    bool RebootRequired, IReadOnlyList<string> Cves, IReadOnlyList<string> KnowledgeBaseIds,
    int? ServerSelection = null, Guid? ServiceId = null, IReadOnlyList<string>? SupersededUpdateIds = null);

/// <summary>Reads local WUA metadata without modifying update settings or installing updates.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsUpdateReader
{
    private const int MaximumUpdates = 20_000;
    private const int MaximumReferences = 5_000;
    private const int MaximumText = 2_048;
    private const int MaximumTotalReferences = 100_000;
    private const int MaximumTotalCharacters = 8_000_000;
    private static readonly SemaphoreSlim SearchGate = new(1, 1);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public static Task<InventoryReadResult<WindowsUpdateObservation>> ReadAsync(CancellationToken token) =>
        RunBoundedAsync(ReadCoreAsync, SearchTimeout, SearchGate, token);

    internal static async Task<InventoryReadResult<WindowsUpdateObservation>> RunBoundedAsync(
        Func<CancellationToken, Task<InventoryReadResult<WindowsUpdateObservation>>> read, TimeSpan timeout,
        SemaphoreSlim gate, CancellationToken token)
    {
        if (!await gate.WaitAsync(timeout, token).ConfigureAwait(false)) return new(InventoryReadStatus.Error, []);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var deadlineToken = deadline.Token;
        var operation = Task.Run(() => read(deadlineToken), CancellationToken.None);
        _ = operation.ContinueWith(static (completed, state) =>
        {
            _ = completed.Exception; // Observe errors even if the caller has already timed out.
            var (source, lockGate) = ((CancellationTokenSource, SemaphoreSlim))state!;
            source.Dispose();
            lockGate.Release();
        }, (deadline, gate), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await operation.WaitAsync(deadlineToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(InventoryReadStatus.Error, []); }
    }

    private static async Task<InventoryReadResult<WindowsUpdateObservation>> ReadCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object? session = null, searcher = null, job = null, result = null, updates = null;
        var callback = new SearchCallback();
        var started = false;
        try
        {
            session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
            searcher = ((dynamic)session).CreateUpdateSearcher();
            ((dynamic)searcher).Online = false;
            ((dynamic)searcher).IncludePotentiallySupersededUpdates = true;
            var serverSelection = (int)((dynamic)searcher).ServerSelection;
            if (serverSelection is < 0 or > 3) return new(InventoryReadStatus.Partial, []);
            var rawServiceId = (string?)((dynamic)searcher).ServiceID;
            Guid? serviceId = Guid.TryParse(rawServiceId, out var parsedServiceId) ? parsedServiceId : null;
            token.ThrowIfCancellationRequested();
            // WUA permits OR only at the top level; both observations still come from an incomplete offline cache.
            job = ((dynamic)searcher).BeginSearch("(IsInstalled=1 and Type='Software') or (IsInstalled=0 and Type='Software')", callback, null);
            started = true;
            using var registration = token.Register(static state =>
            {
                try { ((dynamic)state!).RequestAbort(); }
                catch (COMException) { /* EndSearch still owns completion and cleanup. */ }
            }, job);
            // The callback can run before BeginSearch returns. Always finish the native job before release.
            await callback.Done.Task.ConfigureAwait(false);
            result = ((dynamic)searcher).EndSearch((dynamic)job);
            token.ThrowIfCancellationRequested();
            if ((int)((dynamic)result).ResultCode != 2) return new(InventoryReadStatus.Error, []);
            updates = ((dynamic)result).Updates;
            var count = (int)((dynamic)updates).Count;
            if (count < 0 || count > MaximumUpdates) return new(InventoryReadStatus.Partial, []);
            var items = new List<WindowsUpdateObservation>(count);
            var totalReferences = 0;
            var totalCharacters = 0;
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                object? update = null, identity = null, cves = null, kbs = null, superseded = null;
                try
                {
                    update = ((dynamic)updates).Item(i);
                    identity = ((dynamic)update).Identity;
                    cves = ((dynamic)update).CveIDs;
                    kbs = ((dynamic)update).KBArticleIDs;
                    superseded = ((dynamic)update).SupersededUpdateIDs;
                    if (!Guid.TryParse((string)((dynamic)identity).UpdateID, out var id) || id == Guid.Empty ||
                        (int)((dynamic)identity).RevisionNumber is not > 0 ||
                        (string)((dynamic)update).Title is not { Length: > 0 and <= MaximumText } title ||
                        title.Any(char.IsControl) || (totalCharacters += title.Length) > MaximumTotalCharacters ||
                        !TryReadStrings(cves, ref totalReferences, ref totalCharacters, token, out var cveIds) ||
                        !TryReadStrings(kbs, ref totalReferences, ref totalCharacters, token, out var kbIds) ||
                        !TryReadStrings(superseded, ref totalReferences, ref totalCharacters, token, out var rawSuperseded) ||
                        !TryNormalizeIds(rawSuperseded, out var supersededIds))
                        return new(InventoryReadStatus.Partial, []);
                    items.Add(new(id, (int)((dynamic)identity).RevisionNumber, title,
                        (bool)((dynamic)update).IsInstalled, (bool)((dynamic)update).RebootRequired, cveIds, kbIds,
                        serverSelection, serviceId, supersededIds));
                }
                finally { Release(superseded); Release(kbs); Release(cves); Release(identity); Release(update); }
            }
            // Offline cache freshness and completeness are not established by a successful search.
            return new(InventoryReadStatus.Partial, items);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is COMException or UnauthorizedAccessException)
        {
            token.ThrowIfCancellationRequested();
            throw;
        }
        catch (UnauthorizedAccessException) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005) { return new(InventoryReadStatus.PermissionDenied, []); }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidCastException)
        { return new(InventoryReadStatus.Error, []); }
        finally
        {
            if (started && job is not null)
            {
                try { ((dynamic)job).CleanUp(); }
                catch (COMException) { }
            }
            Release(updates); Release(result); Release(job); Release(searcher); Release(session);
            GC.KeepAlive(callback);
        }
    }

    private static bool TryReadStrings(object collection, ref int totalReferences, ref int totalCharacters, CancellationToken token, out IReadOnlyList<string> values)
    {
        var count = (int)((dynamic)collection).Count;
        if (count < 0 || count > MaximumReferences || (totalReferences += count) > MaximumTotalReferences) { values = []; return false; }
        var items = new string[count];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (((dynamic)collection).Item(i) is not string { Length: > 0 and <= MaximumText } value || value.Any(char.IsControl) ||
                (totalCharacters += value.Length) > MaximumTotalCharacters)
            { values = []; return false; }
            items[i] = value;
        }
        values = items;
        return true;
    }

    internal static bool TryNormalizeIds(IReadOnlyList<string> values, out IReadOnlyList<string> normalized)
    {
        var ids = new List<string>(values.Count);
        var unique = new HashSet<Guid>();
        for (var i = 0; i < values.Count; i++)
        {
            if (!Guid.TryParse(values[i], out var id) || id == Guid.Empty) { normalized = []; return false; }
            if (unique.Add(id)) ids.Add(id.ToString("D"));
        }
        normalized = ids;
        return true;
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class SearchCallback
    {
        internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        [DispId(0)] public void Invoke(object job, object args) => Done.TrySetResult();
    }
}
