using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MsrcCvrfReaderTests
{
    internal const string Example = """
        {"DocumentTracking":{"Identification":{"ID":{"Value":"2025-Nov"}},"CurrentReleaseDate":"2025-11-01T00:00:00"},
         "ProductTree":{"Branch":[{"Items":[{"Name":"Windows","Items":[
           {"ProductID":"11923","Value":"Windows Server 2022"},
           {"ProductID":"11924","Value":"Windows Server 2022 (Server Core installation)"}]}]}]},
         "Vulnerability":[{"CVE":"CVE-2025-1234","ProductStatuses":[{"Type":3,"ProductID":["11923"]}],
          "Threats":[{"Type":3,"Description":{"Value":"Critical"},"ProductID":["11923"]},{"Type":3,"Description":{"Value":"Moderate"},"ProductID":["11924"]}],"Remediations":[
           {"Type":2,"Description":{"Value":"1234567"},"FixedBuild":"10.0.20348.3000",
            "Supercedence":"1234566","SubType":"Security Update","ProductID":["11923","11924"]},
           {"Type":3,"ProductID":["11923"]}]}]}
        """;

    [TestMethod]
    [DataRow("Customer Action Required", "Yes", true)]
    [DataRow("Customer Action Required", "No", false)]
    [DataRow("Other", "Yes", false)]
    [DataRow("Customer Action Required", "Unknown", false)]
    public void Read_OnlyFlagsAnExplicitCustomerActionNotice(string title, string value, bool expected)
    {
        var input = Example.Replace("\"CVE\":\"CVE-2025-1234\"", $"\"CVE\":\"CVE-2025-1234\",\"Notes\":[{{\"Type\":6,\"Title\":\"{title}\",\"Value\":\"{value}\"}}]");
        var advisory = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100).Advisories.Single();
        Assert.AreEqual(expected, advisory.HasCustomerActionNotice);
    }

    [TestMethod]
    public void Read_PreservesProductScopedVendorFixWithoutInferringInstalledState()
    {
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(Example), 10000, 100);
        Assert.AreEqual("2025-Nov", result.Id);
        Assert.IsTrue(result.HasVulnerabilitySection);
        Assert.AreEqual("Windows Server 2022", result.Products["11923"]);
        var advisory = result.Advisories.Single();
        Assert.AreEqual("CVE-2025-1234", advisory.Cve);
        Assert.AreEqual(new MsrcProductStatus("11923", 3), advisory.ProductStatuses.Single());
        CollectionAssert.AreEqual(new[] { new MsrcSeverity("11923", "Critical"), new MsrcSeverity("11924", "Moderate") }, advisory.Severities!.ToArray());
        Assert.HasCount(2, advisory.Remediations);
        Assert.AreEqual(new MsrcRemediation("11923", "1234567", "10.0.20348.3000", "1234566", "Security Update"), advisory.Remediations[0]);
    }

    [TestMethod]
    public void Read_RejectsUnknownProductsAndExcessiveInput()
    {
        var input = Encoding.UTF8.GetBytes(Example);
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCvrfReader.Read(input, input.Length - 1, 100));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCvrfReader.Read(input, 10000, 1));
        var unknown = Encoding.UTF8.GetBytes(Example.Replace("\"11923\",\"11924\"", "\"not-in-tree\""));
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCvrfReader.Read(unknown, 10000, 100));
    }

    [TestMethod]
    public void Read_AcceptsEmptyOptionalFieldsAndFullProductNames()
    {
        var input = Example.Replace("\"Branch\":[", "\"FullProductName\":[{\"ProductID\":\"extra\",\"Value\":\"Extra product\"}],\"Branch\":[")
            .Replace("\"FixedBuild\":\"10.0.20348.3000\"", "\"FixedBuild\":\"\"")
            .Replace("\"11923\",\"11924\"", "\"extra\",\"11924\"");
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100);
        Assert.AreEqual("Extra product", result.Products["extra"]);
        Assert.IsNull(result.Advisories.Single().Remediations[0].FixedBuild);
        Assert.AreEqual("extra", result.Advisories.Single().Remediations[0].ProductId);
    }

    [TestMethod]
    public void Read_RejectsDuplicatePropertiesAndRetainsDuplicateAdvisories()
    {
        var duplicateProperty = Example.Replace("\"CVE\":\"CVE-2025-1234\"", "\"CVE\":\"CVE-2025-1234\",\"CVE\":\"CVE-2025-0000\"");
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(duplicateProperty), 10000, 100));
        var duplicateCve = Example.Replace("\"Vulnerability\":[", "\"Vulnerability\":[{\"CVE\":\"CVE-2025-1234\",\"Remediations\":[]},");
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(duplicateCve), 10000, 100);
        Assert.Contains("CVE-2025-1234", result.DuplicateAdvisoryIds);
        Assert.AreEqual(2, result.Advisories.Count(x => x.Cve == "CVE-2025-1234"));
    }

    [TestMethod]
    public void Read_MissingVulnerabilitySectionRemainsExplicit()
    {
        var input = """{"DocumentTracking":{"Identification":{"ID":{"Value":"2004-Sep"}},"CurrentReleaseDate":"2025-11-01T00:00:00"},"ProductTree":{"Branch":[],"FullProductName":[]}}""";
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 10);
        Assert.IsFalse(result.HasVulnerabilitySection);
        Assert.IsEmpty(result.Advisories);
    }

    [TestMethod]
    public void Read_AcceptsBoundedLegacySupersedenceList()
    {
        var input = Example.Replace("1234566", new string('x', 492));
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100);
        Assert.HasCount(492, result.Advisories.Single().Remediations[0].Supersedence!);
        var multiline = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(Example.Replace("1234566", "1234566\\n")), 10000, 100);
        Assert.AreEqual("1234566\n", multiline.Advisories.Single().Remediations[0].Supersedence);
    }

    [TestMethod]
    public void Read_TrimsWhitespaceAroundFixedBuildWithoutAcceptingEmbeddedControls()
    {
        var input = Example.Replace("10.0.20348.3000", "\\t\\n10.0.20348.3000");
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100);
        Assert.AreEqual("10.0.20348.3000", result.Advisories.Single().Remediations[0].FixedBuild);
        var invalid = Example.Replace("10.0.20348.3000", "10.0.\\n20348.3000");
        Assert.ThrowsExactly<InvalidDataException>(() => MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(invalid), 10000, 100));
    }

    [TestMethod]
    public void Read_RetainsUnscopedProductStatusAsIncompleteEvidence()
    {
        var input = Example.Replace("{\"Type\":3,\"ProductID\":[\"11923\"]}", "{\"Type\":3}");
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100);
        Assert.IsTrue(result.Advisories.Single().HasUnscopedStatus);
        Assert.IsEmpty(result.Advisories.Single().ProductStatuses);
    }

    [TestMethod]
    public void Read_MarksConflictingProductNamesAsAmbiguous()
    {
        var input = Example.Replace("\"Branch\":[", "\"FullProductName\":[{\"ProductID\":\"11923\",\"Value\":\"Different name\"}],\"Branch\":[");
        var result = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(input), 10000, 100);
        Assert.Contains("11923", result.AmbiguousProductIds);
    }
}
