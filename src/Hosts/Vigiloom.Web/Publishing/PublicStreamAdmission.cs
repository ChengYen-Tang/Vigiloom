namespace Vigiloom.Web.Publishing;

/// <summary>Bounds anonymous status subscriptions within this Web process.</summary>
internal sealed class PublicStreamAdmission : IDisposable
{
    public PublicStreamAdmission() : this(64, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5), TimeProvider.System) { }

    internal PublicStreamAdmission(int capacity, TimeSpan interval, TimeSpan lifetime, TimeProvider clock, TimeSpan? readTimeout = null)
    {
        Slots = new SemaphoreSlim(capacity, capacity);
        Interval = interval;
        Lifetime = lifetime;
        Clock = clock;
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(15);
    }

    internal SemaphoreSlim Slots { get; }
    internal TimeSpan Interval { get; }
    internal TimeSpan Lifetime { get; }
    internal TimeProvider Clock { get; }
    internal TimeSpan ReadTimeout { get; }
    public void Dispose() => Slots.Dispose();
}
