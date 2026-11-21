using System.Diagnostics;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Owns the disposable hosted-runner service installation for one integration test.</summary>
internal sealed class SystemdAgentProcess(Process process, Task<string> errors) : IAsyncDisposable
{
    internal static async Task<SystemdAgentProcess> InstallAsync(string script, string bundle, string certificate,
        Uri server, Guid deviceId, string fingerprint, string enrollment, CancellationToken token)
    {
        var start = new ProcessStartInfo("sudo")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-n", "--preserve-env=GITHUB_ACTIONS,RUNNER_ENVIRONMENT", "bash", script, bundle, certificate })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated Agent installer.");
        var errors = process.StandardError.ReadToEndAsync();
        var owner = new SystemdAgentProcess(process, errors);
        try
        {
            // Enrollment data goes through stdin, never the process command line or test output.
            foreach (var line in new[] { server.ToString(), deviceId.ToString("D"), fingerprint, enrollment })
                await process.StandardInput.WriteLineAsync(line.AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            if (await process.StandardOutput.ReadLineAsync(token) != "READY")
                throw new InvalidOperationException("The isolated Agent installation did not become ready.");
            return owner;
        }
        catch
        {
            await owner.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await process.WaitForExitAsync(deadline.Token);
            }
            var diagnostic = await errors;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The isolated Agent installation or cleanup failed: {diagnostic}");
        }
        finally { process.Dispose(); }
    }
}
