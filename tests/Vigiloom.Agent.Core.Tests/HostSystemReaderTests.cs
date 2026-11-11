using System.Runtime.InteropServices;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Inventory;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class HostSystemReaderTests
{
    [TestMethod]
    public void Read_PreservesOperatingSystemAndProcessArchitectureSeparately()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var result = HostSystemReader.Read(new FakeTimeProvider(now));
        Assert.AreEqual(now, result.ObservedAt);
        Assert.AreEqual(RuntimeInformation.OSArchitecture.ToString(), result.OsArchitecture);
        Assert.AreEqual(RuntimeInformation.ProcessArchitecture.ToString(), result.ProcessArchitecture);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.HostName));
        Assert.IsGreaterThan(0, result.AvailableProcessorCount);
    }
}
