namespace Vigiloom.Worker.Hosting;

internal static class SecurityCutover
{
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue("Services:Security:IntelligenceEnabled", false) &&
        !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
}
