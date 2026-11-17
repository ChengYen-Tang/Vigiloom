namespace Vigiloom.Contracts.Intelligence;

/// <summary>Time bounds shared by correlation and host-log retention.</summary>
public static class SigmaCorrelationLimits
{
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumLateness = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromSeconds(30);
    public static TimeSpan RequiredRetention => MaximumWindow + MaximumLateness + MaximumFutureSkew;
}
