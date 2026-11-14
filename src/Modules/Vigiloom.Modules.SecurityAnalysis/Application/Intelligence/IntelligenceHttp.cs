using System.Net;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>HTTP transport policies for centrally fetched intelligence sources.</summary>
public static class IntelligenceHttp
{
    public const string KevClientName = "cisa-kev";
    public const string AdvisoryClientName = "github-advisories";
    public const string RepositoryClientName = "intelligence-repositories";

    public static HttpMessageHandler CreateKevHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    public static HttpMessageHandler CreateAdvisoryHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    };
}
