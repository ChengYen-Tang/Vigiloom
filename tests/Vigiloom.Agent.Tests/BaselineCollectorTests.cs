using Vigiloom.Agent.Hosting.Baseline;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class BaselineCollectorTests
{
    [TestMethod]
    public async Task Collector_DoesNotExecuteUnsupportedCustomOperation()
    {
        var check = new BaselineCheck($"custom-{Guid.CreateVersion7():D}", "Check", "linux", ["ubuntu:24.04"],
            "run-command", "/etc/passwd", "0600", "https://example.test/rule", "User-defined");
        Assert.IsFalse(CustomBaselineCheck.IsValid(check));
        var result = await BaselineCollector.ReadAsync(check, CancellationToken.None);
        Assert.AreEqual(BaselineOutcome.Unknown, result.Result.Outcome);
    }

    [TestMethod]
    public async Task Collector_DoesNotExecuteAnUnrecognizedTarget()
    {
        var check = new BaselineCheck(BaselineSources.LinuxShadowCheck, "Shadow permissions", "linux", ["ubuntu:24.04"],
            BaselineSources.LinuxShadowOperation, "/etc/passwd", "0640-or-stricter", "https://example.test/rule", "BSD-3-Clause");
        var result = await BaselineCollector.ReadAsync(check, CancellationToken.None);
        Assert.AreEqual(BaselineOutcome.Unknown, result.Result.Outcome);
        Assert.AreEqual("Unsupported baseline check", result.Result.Evidence);
    }
}
