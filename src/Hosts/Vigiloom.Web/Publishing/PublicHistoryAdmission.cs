namespace Vigiloom.Web.Publishing;

/// <summary>Bounds concurrent anonymous history queries in this Web process.</summary>
internal sealed class PublicHistoryAdmission : IDisposable
{
    public PublicHistoryAdmission() : this(TimeSpan.FromSeconds(15), TimeProvider.System) { }
    internal PublicHistoryAdmission(TimeSpan timeout, TimeProvider clock) => (Timeout, Clock) = (timeout, clock);
    internal SemaphoreSlim Gate { get; } = new(2, 2);
    internal TimeSpan Timeout { get; }
    internal TimeProvider Clock { get; }
    public void Dispose() => Gate.Dispose();
}
