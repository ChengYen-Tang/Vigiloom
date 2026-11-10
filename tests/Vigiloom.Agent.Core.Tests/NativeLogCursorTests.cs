using Vigiloom.Agent.Core.Logs;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class NativeLogCursorTests
{
    [TestMethod]
    public void Cursor_DistinguishesNativeValueFromRestartMarker()
    {
        Assert.IsNull(NativeLogCursor.Decode(null));
        Assert.IsNull(NativeLogCursor.Decode(NativeLogCursor.Restart));
        Assert.AreEqual("r", NativeLogCursor.Decode(NativeLogCursor.Encode("r")));
        Assert.AreEqual("p:value", NativeLogCursor.Decode(NativeLogCursor.Encode("p:value")));
        Assert.ThrowsExactly<InvalidDataException>(() => NativeLogCursor.Decode("broken"));
        Assert.ThrowsExactly<InvalidDataException>(() => NativeLogCursor.Encode(new string('a', 4096)));
    }
}
