using System.Runtime.InteropServices;

namespace Vigiloom.SpeedLab;

// Temporary timing precision for the offline experiment; never used by the product.
internal sealed partial class TimerPrecision : IDisposable
{
    private readonly bool requested;

    private TimerPrecision(bool requested) => this.requested = requested;

    public static TimerPrecision Request()
    {
        if (!OperatingSystem.IsWindows()) return new(false);
        if (Begin(1) != 0) throw new InvalidOperationException("Unable to request one-millisecond timer precision.");
        return new(true);
    }

    public void Dispose()
    {
        if (requested && End(1) != 0) throw new InvalidOperationException("Unable to restore timer precision.");
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint Begin(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint End(uint period);
}
