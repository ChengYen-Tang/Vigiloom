using Vigiloom.Agent.Core.Identity;

namespace Vigiloom.Agent.Hosting;

/// <summary>Retries an installer's one-time enrollment as the Agent service account.</summary>
internal sealed class AgentEnrollmentWorker(AgentEnrollmentBootstrap bootstrap, IConfiguration configuration,
    TimeProvider clock, ILogger<AgentEnrollmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var directory = configuration["Vigiloom:Agent:StateDirectory"];
        var server = configuration["Vigiloom:Agent:Server"];
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) ||
            !Uri.TryCreate(server, UriKind.Absolute, out var uri) ||
            !File.Exists(Path.Combine(directory, AgentEnrollmentBootstrap.FileName))) return;
        var vault = new IdentityVault(directory);
        string? previousError = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (directory != configuration["Vigiloom:Agent:StateDirectory"] || server != configuration["Vigiloom:Agent:Server"])
                    return;
                if (await bootstrap.TryOnceAsync(vault, uri, stoppingToken))
                    logger.LogInformation("Agent installation enrollment completed.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
            {
                if (previousError != error.GetType().Name)
                {
                    logger.LogWarning("Agent installation enrollment is pending: {ErrorType}.", error.GetType().Name);
                    previousError = error.GetType().Name;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
        }
    }
}
