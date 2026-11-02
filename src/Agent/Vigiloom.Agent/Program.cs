using Vigiloom.Agent.Hosting;
using Vigiloom.Agent.Core.Identity;
using Microsoft.Extensions.DependencyInjection;

var builder = AgentHost.CreateBuilder(args);
using var host = builder.Build();
if (args.Length == 1 && args[0] is "enroll" or "reset-pending-enrollment" or "rotate-credential")
{
    var url = builder.Configuration["Vigiloom:Agent:Server"];
    var directory = builder.Configuration["Vigiloom:Agent:StateDirectory"];
    if (!Uri.TryCreate(url, UriKind.Absolute, out var server) || string.IsNullOrWhiteSpace(directory))
        throw new InvalidOperationException("Configure Vigiloom:Agent:Server and an absolute Vigiloom:Agent:StateDirectory before managing identity.");
    var client = host.Services.GetRequiredService<IdentityClient>();
    var vault = new IdentityVault(directory);
    if (args[0] == "rotate-credential")
    {
        await client.RotateAsync(vault, server, CancellationToken.None);
        Console.WriteLine("Agent credential rotation confirmed.");
        return;
    }
    var reset = args[0] == "reset-pending-enrollment";
    if (reset)
    {
        Console.WriteLine("Only continue after an administrator has revoked the previous enrollment and any Agent it created. This discards the pending local machine secret.");
        Console.Write("Type RESET to confirm: ");
        if (Console.ReadLine() != "RESET") return;
    }
    Console.Write(reset ? "Enter newly issued enrollment data: " : "Enter enrollment data (or press Enter to resume a pending registration): ");
    var secret = ReadSecret();
    Console.WriteLine();
    if (reset) await client.ResetPendingAsync(vault, server, secret ?? "", CancellationToken.None);
    var id = await client.EnrollAsync(vault, server, string.IsNullOrWhiteSpace(secret) ? null : secret, CancellationToken.None);
    Console.WriteLine($"Agent identity {id:D} registered.");
    return;
}
await host.RunAsync();

static string? ReadSecret()
{
    if (Console.IsInputRedirected)
    {
        var line = Console.ReadLine();
        if (line?.Length > 256) throw new InvalidOperationException("Enrollment data is too long.");
        return line;
    }
    var value = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) return value.ToString();
        if (key.Key == ConsoleKey.Backspace) { if (value.Length != 0) value.Length--; }
        else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        if (value.Length > 256) throw new InvalidOperationException("Enrollment data is too long.");
    }
}
