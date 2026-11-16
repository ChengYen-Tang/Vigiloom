using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
public sealed class UpdateHandoffTests(TestContext context)
{
    [TestMethod]
    public async Task CredentialIsValidatedAndExcludedFromDisplay()
    {
        var credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}";
        var message = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = credential };
        Assert.IsTrue(message.IsValid());
        Assert.DoesNotContain(credential, message.ToString());
        using var stream = new MemoryStream();
        await BoundedJsonFrame.WriteAsync(stream, message, context.CancellationToken);
        stream.Position = 0;
        var read = await BoundedJsonFrame.ReadAsync<UpdateHandoff>(stream, context.CancellationToken);
        Assert.IsTrue(read.IsValid());
        Assert.AreEqual(message.UpdateId, read.UpdateId);
        Assert.AreEqual(credential, read.Credential);
        Assert.IsFalse(new UpdateHandoff { UpdateId = Guid.Empty, Credential = credential }.IsValid());
        Assert.IsFalse(new UpdateHandoff { UpdateId = message.UpdateId, Credential = credential + "x" }.IsValid());
    }

    [TestMethod]
    public async Task InvalidFramesAndUnexpectedFieldsAreRejected()
    {
        var token = context.CancellationToken;
        using var oversized = new MemoryStream(new byte[] { 0, 0, 0x20, 0 });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BoundedJsonFrame.ReadAsync<UpdateHandoff>(oversized, token));
        using var truncated = new MemoryStream(new byte[] { 0, 0, 0, 5, 1 });
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => BoundedJsonFrame.ReadAsync<UpdateHandoff>(truncated, token));
        var json = Encoding.UTF8.GetBytes("{\"updateId\":\"00000000-0000-0000-0000-000000000000\",\"credential\":\"x\",\"path\":\"unexpected\"}");
        var packet = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, json.Length);
        json.CopyTo(packet, 4);
        using var unexpected = new MemoryStream(packet);
        await Assert.ThrowsExactlyAsync<JsonException>(() => BoundedJsonFrame.ReadAsync<UpdateHandoff>(unexpected, token));
    }
}
