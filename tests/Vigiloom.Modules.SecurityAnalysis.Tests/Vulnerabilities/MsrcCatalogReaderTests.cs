using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MsrcCatalogReaderTests
{
    private const string Catalog = """
        {"value":[{"ID":"2017-May-B","CurrentReleaseDate":"2025-11-01T00:00:00Z"},
                  {"ID":"2018-FEB","CurrentReleaseDate":"2025-11-02T00:00:00Z"}]}
        """;

    [TestMethod]
    public void Read_PreservesActualNonstandardDocumentIdentifiers()
    {
        var entries = MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog), 10000, 10);
        Assert.AreEqual("2017-May-B", entries[0].Id);
        Assert.AreEqual("2018-FEB", entries[1].Id);
        Assert.IsFalse(MsrcCatalogReader.ValidId("2025-Nov/../../evil"));
        Assert.IsFalse(MsrcCatalogReader.ValidId("0000-Nov"));
        Assert.IsFalse(MsrcCatalogReader.ValidId("2025-Xxx"));
    }

    [TestMethod]
    public void Read_RejectsPartialAndAmbiguousCatalogs()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog.Replace("{\"value\"", "{\"@odata.nextLink\":\"next\",\"value\"")), 10000, 10));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog.Replace("2018-FEB", "2017-May-B")), 10000, 10));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog.Replace("\"ID\":\"2018-FEB\"", "\"ID\":\"2018-FEB\",\"ID\":\"2025-Nov\"")), 10000, 10));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog), 10000, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes("{\"value\":[]}"), 10000, 10));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog.Replace("2025-11-01T00:00:00Z", "2025-11-01T00:00:00")), 10000, 10));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCatalogReader.Read(Encoding.UTF8.GetBytes(Catalog.Replace("2025-11-01T00:00:00Z", "2025-11-01T00:00:00+08:00")), 10000, 10));
    }
}
