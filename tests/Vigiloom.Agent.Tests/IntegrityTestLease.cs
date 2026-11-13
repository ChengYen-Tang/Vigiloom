namespace Vigiloom.Agent.Tests;

internal static class IntegrityTestLease
{
    public static async Task<T> ReadAsync<T>(Func<Task<T>> read, CancellationToken token)
    {
        while (true)
        {
            try { return await read(); }
            catch (IOException ex) when (OperatingSystem.IsWindows() ? (ex.HResult & 0xffff) is 32 or 33 :
                OperatingSystem.IsLinux() && (ex.HResult & 0xffff) == 11)
            { await Task.Delay(50, token); }
        }
    }
}
