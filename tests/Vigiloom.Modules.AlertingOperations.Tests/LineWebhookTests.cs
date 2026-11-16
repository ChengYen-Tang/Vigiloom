using System.Security.Cryptography;
using System.Text;
using Line.OpenApi.Messaging.Webhook;
using Vigiloom.Modules.AlertingOperations.Application.Line;

namespace Vigiloom.Modules.AlertingOperations.Tests;

[TestClass]
public sealed class LineWebhookTests
{
    [TestMethod]
    public async Task OfficialVerificationExampleAcceptsEmptyEvents()
    {
        var body = Encoding.UTF8.GetBytes("{\"destination\":\"U8e742f61d673b39c7fff3cecb7536ef0\",\"events\":[]}");
        var groups = await LineWebhook.ReadGroupsAsync("8c570fa6dd201bb328f1c1eac23a96d8", body,
            "GhRKmvmHys4Pi8DxkF4+EayaH0OqtJtaZxgTD9fMDLs=", CancellationToken.None);
        Assert.IsEmpty(groups);
    }

    [TestMethod]
    public async Task VerifiedMultiEventBodyOnlyReturnsDistinctGroupCandidates()
    {
        const string secret = "test-channel-secret";
        var body = Encoding.UTF8.GetBytes("""
            {"destination":"Utest","events":[
              {"type":"join","source":{"type":"group","groupId":"Cfirst"}},
              {"type":"message","source":{"type":"group","groupId":"Cfirst"},"message":{"id":"1","type":"image"}},
              {"type":"message","source":{"type":"group","groupId":"Csecond"},"message":{"id":"2","type":"text","text":"do not bind"}},
              {"type":"message","source":{"type":"user","userId":"Ufirst"},"message":{"id":"3","type":"text","text":"hello"}},
              {"type":"leave","source":{"type":"group","groupId":"Cleft"}}
            ]}
            """);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        var groups = await LineWebhook.ReadGroupsAsync(secret, body, signature, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "Cfirst", "Csecond" }, groups.ToArray());
        var redelivery = await LineWebhook.ReadGroupsAsync(secret, body, signature, CancellationToken.None);
        CollectionAssert.AreEquivalent(groups.ToArray(), redelivery.ToArray());
    }

    [TestMethod]
    public async Task ChangedBodyIsRejectedBeforeProcessing()
    {
        var body = Encoding.UTF8.GetBytes("{\"events\":[]}");
        await Assert.ThrowsExactlyAsync<WebhookSignatureException>(() =>
            LineWebhook.ReadGroupsAsync("test-channel-secret", body, "invalid", CancellationToken.None));
    }
}
