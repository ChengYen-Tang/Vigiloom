namespace Vigiloom.Testing;

[TestClass]
public static class TestEnvironment
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context) =>
        Environment.SetEnvironmentVariable("Vigiloom__SelfTelemetry__Enabled", "false");
}
