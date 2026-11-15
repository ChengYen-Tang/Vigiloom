using System.Net;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>HTTP transport policies for centrally fetched intelligence sources.</summary>
public static class IntelligenceHttp
{
    public const string KevClientName = "cisa-kev";
    public const string AdvisoryClientName = "github-advisories";
    public const string RepositoryClientName = "intelligence-repositories";
    public const string DebianClientName = "debian-security";
    public const string UbuntuClientName = "ubuntu-osv";
    public const string MsrcClientName = "msrc-cvrf";
    public const string EpssClientName = "first-epss";
    public const string MicrosoftCatalogClientName = "microsoft-update-catalog";

    public static HttpMessageHandler CreateMicrosoftCatalogHandler() => new MicrosoftCatalogHostGuard
    {
        InnerHandler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }
    };

    public static HttpMessageHandler CreateKevHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    };

    public static HttpMessageHandler CreateDebianHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip
    };

    public static HttpMessageHandler CreateAdvisoryHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    };
}
