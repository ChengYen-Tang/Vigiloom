using System.Net;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthResponseTests
{
    [TestMethod]
    [DataRow(200, "Healthy", 0)]
    [DataRow(200, " degraded \r\n", 1)]
    [DataRow(200, "Unhealthy", 2)]
    [DataRow(503, "Unhealthy", 2)]
    public void Parse_RecognizesExplicitApplicationHealth(int status, string body, int expected)
    {
        Assert.AreEqual((ReportedHealth)expected, HealthResponse.Parse((HttpStatusCode)status, body));
    }

    [TestMethod]
    [DataRow(503, "Healthy")]
    [DataRow(503, "Degraded")]
    [DataRow(500, "Unhealthy")]
    [DataRow(302, "Healthy")]
    [DataRow(200, "{\"status\":\"Healthy\"}")]
    [DataRow(200, "")]
    [DataRow(200, "Healthy because it responded")]
    public void Parse_InvalidStatusOrBody_ReturnsNoApplicationHealth(int status, string body)
    {
        Assert.IsNull(HealthResponse.Parse((HttpStatusCode)status, body));
    }
}
