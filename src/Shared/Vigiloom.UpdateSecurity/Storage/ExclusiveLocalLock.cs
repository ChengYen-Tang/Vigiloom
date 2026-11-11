using System.Diagnostics;

namespace Vigiloom.UpdateSecurity;

internal static class ExclusiveLocalLock
{
    public static async Task<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            { await Task.Delay(100, cancellationToken); }
        }
    }
}
