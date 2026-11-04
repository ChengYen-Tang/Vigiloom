using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class ResourceIdentityTests
{
    [TestMethod]
    public void Read_UsesBoundedServiceMetadataAndPreservesInstanceIdentity()
    {
        var resource = ResourceOf(("service.name", " orders "), ("deployment.environment.name", " prod "),
            ("service.instance.id", "Node A "), ("host.name", "untrusted-host"));
        Assert.IsTrue(ResourceIdentity.TryRead(resource, out var identity));
        Assert.IsNotNull(identity);
        Assert.AreEqual("orders", identity.ServiceKey);
        Assert.AreEqual("prod", identity.Environment);
        Assert.AreEqual("Node A ", identity.InstanceId);
    }

    [TestMethod]
    public void Read_RejectsAmbiguousOrInvalidIdentity()
    {
        Assert.IsFalse(ResourceIdentity.TryRead(ResourceOf(("service.name", "a"), ("service.name", "b"), ("deployment.environment.name", "prod")), out _));
        Assert.IsFalse(ResourceIdentity.TryRead(ResourceOf(("service.name", "a")), out _));
        Assert.IsFalse(ResourceIdentity.TryRead(ResourceOf(("service.name", new string('a', 129)), ("deployment.environment.name", "prod")), out _));
        Assert.IsFalse(ResourceIdentity.TryRead(ResourceOf(("service.name", "a"), ("deployment.environment.name", "prod"), ("service.instance.id", "\ud800")), out _));
        Assert.IsFalse(ResourceIdentity.TryRead(ResourceOf(("service.name", "api"), ("service.namespace", new string('a', 129)), ("deployment.environment.name", "prod")), out _));
        var wrongType = ResourceOf(("deployment.environment.name", "prod"));
        wrongType.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { IntValue = 1 } });
        Assert.IsFalse(ResourceIdentity.TryRead(wrongType, out _));
    }

    private static Resource ResourceOf(params (string Key, string Value)[] attributes)
    {
        var resource = new Resource();
        foreach (var (key, value) in attributes)
            resource.Attributes.Add(new KeyValue { Key = key, Value = new AnyValue { StringValue = value } });
        return resource;
    }
}
