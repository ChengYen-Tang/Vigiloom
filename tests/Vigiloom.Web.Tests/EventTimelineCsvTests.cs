using System.Text;
using Vigiloom.Contracts.Events;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class EventTimelineCsvTests
{
    [TestMethod]
    public void Encode_EscapesSpreadsheetFormulasAndQuotedValues()
    {
        var entry = new EventTimelineEntry("host-log", DateTimeOffset.UtcNow, Guid.CreateVersion7(), "device", " =1+1", "a,\"b\"", "/devices/test");
        var csv = Encoding.UTF8.GetString(EventTimelineCsv.Encode([entry]));
        Assert.Contains("\"' =1+1\"", csv);
        Assert.Contains("\"a,\"\"b\"\"\"", csv);
    }
}
