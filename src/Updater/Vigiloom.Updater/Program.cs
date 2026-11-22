using Vigiloom.Updater.Hosting;

if (args is ["recovery-status", ..])
{
    if (args is not [_, var stateDirectory])
    {
        Console.Error.WriteLine("Usage: recovery-status <absolute-state-directory>");
        Environment.ExitCode = 2;
        return;
    }
    var status = await RecoveryStatusReader.ReadAsync(stateDirectory, CancellationToken.None);
    Console.WriteLine(status.ToString().ToLowerInvariant());
    Environment.ExitCode = (int)status;
    return;
}

if (args is ["check-runtime"])
{
    _ = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    Console.WriteLine("Updater runtime check passed.");
    return;
}

var builder = UpdaterHost.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();
