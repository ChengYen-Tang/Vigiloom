namespace Vigiloom.Web.Publishing;

/// <summary>Bounds direct anonymous reads from interactive server circuits.</summary>
internal sealed class PublicStatusAdmission : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(32, 32);
    public void Dispose() => Gate.Dispose();
}
