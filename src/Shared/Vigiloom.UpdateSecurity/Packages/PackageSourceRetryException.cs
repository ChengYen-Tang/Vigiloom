namespace Vigiloom.UpdateSecurity;

/// <summary>The source requested a delay that callers should honor before scheduling another attempt.</summary>
public sealed class PackageSourceRetryException(System.Net.HttpStatusCode status, TimeSpan retryAfter)
    : HttpRequestException("The package source requested a retry.", null, status)
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}
