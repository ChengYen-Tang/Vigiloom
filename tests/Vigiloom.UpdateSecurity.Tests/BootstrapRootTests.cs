using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tuf;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class BootstrapRootTests(TestContext context)
{
    [TestMethod]
    public async Task Validate_AcceptsSelfSignedRootAndRejectsModifiedSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new TestRepository(key).Root(1, key);
        await BootstrapRoot.ValidateAsync(root, context.CancellationToken);

        var tampered = JsonNode.Parse(root)!.AsObject();
        tampered["signed"]!["version"] = 2;
        await Assert.ThrowsAsync<TufException>(() => BootstrapRoot.ValidateAsync(Encoding.UTF8.GetBytes(tampered.ToJsonString()), context.CancellationToken));
    }

    [TestMethod]
    public async Task Validate_RejectsMalformedRoot()
    {
        var invalid = Encoding.UTF8.GetBytes("""{"signed":{"_type":"root","version":1},"signatures":[]}""");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BootstrapRoot.ValidateAsync(invalid, context.CancellationToken));
    }

    [TestMethod]
    public async Task Validate_RejectsSelfSignedRootMissingRequiredRole()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = new TestRepository(key).Root(1, key, omitRole: "timestamp");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BootstrapRoot.ValidateAsync(root, context.CancellationToken));
    }
}
