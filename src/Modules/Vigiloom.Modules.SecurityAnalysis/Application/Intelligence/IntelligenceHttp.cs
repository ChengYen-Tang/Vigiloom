using System.Net;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>HTTP transport policy for the CISA KEV source.</summary>
public static class IntelligenceHttp
{
    public const string KevClientName = "cisa-kev";

    public static HttpMessageHandler CreateKevHandler() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None
    };
}
