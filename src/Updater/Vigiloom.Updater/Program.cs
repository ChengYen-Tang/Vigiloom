using Vigiloom.Updater.Hosting;

if (args is ["check-runtime"])
{
    _ = System.Globalization.CultureInfo.GetCultureInfo("en-US");
    Console.WriteLine("Updater runtime check passed.");
    return;
}

if (args is ["provision-trust", ..])
{
    if (args is not [_, var rootPath, var fingerprint])
        throw new ArgumentException("Usage: provision-trust <absolute-root-file> <sha256-fingerprint>");
    var provisioning = UpdaterHost.CreateBuilder([]);
    await UpdaterProvisioning.ProvisionTrustAsync(provisioning.Configuration, rootPath, fingerprint, CancellationToken.None);
    return;
}
var builder = UpdaterHost.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();
