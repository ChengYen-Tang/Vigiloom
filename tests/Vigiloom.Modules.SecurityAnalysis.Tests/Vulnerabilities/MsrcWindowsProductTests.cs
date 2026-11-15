using System.Text;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MsrcWindowsProductTests
{
    private static InventoryEntry Host(string? installation = "Server Core") => new("host", "machine", "10.0.20348", "X64",
        OperatingSystem: "Windows", ProcessArchitecture: "X64", Runtime: ".NET", AvailableProcessorCount: 2,
        OperatingSystemFamily: "Windows", WindowsEdition: "ServerStandard", WindowsRelease: "21H2", WindowsBuild: 20348,
        WindowsRevision: 3000, WindowsInstallationType: installation);

    [TestMethod]
    public void ProductName_RequiresExactSupportedReleaseAndInstallationType()
    {
        Assert.AreEqual("Windows Server 2022 (Server Core installation)", MsrcWindowsProduct.ProductName(Host()));
        Assert.AreEqual("Windows Server 2022", MsrcWindowsProduct.ProductName(Host("Server")));
        Assert.IsNull(MsrcWindowsProduct.ProductName(Host(null)));
        Assert.IsNull(MsrcWindowsProduct.ProductName(Host() with { Architecture = "Arm64" }));
        Assert.IsNull(MsrcWindowsProduct.ProductName(Host() with { WindowsEdition = "ServerAzure" }));
        Assert.IsNull(MsrcWindowsProduct.ProductName(Host() with { WindowsBuild = 26100 }));
        Assert.IsNull(MsrcWindowsProduct.ProductName(Host() with { WindowsRelease = null }));
    }

    [TestMethod]
    [DataRow(14393, "1607", "Windows Server 2016")]
    [DataRow(17763, "1809", "Windows Server 2019")]
    [DataRow(20348, "21H2", "Windows Server 2022")]
    [DataRow(26100, "24H2", "Windows Server 2025")]
    public void ProductName_MapsSupportedWindowsServerReleases(int build, string release, string expected)
    {
        var host = Host("Server") with { WindowsBuild = build, WindowsRelease = release };
        Assert.AreEqual(expected, MsrcWindowsProduct.ProductName(host));
        Assert.AreEqual(expected + " (Server Core installation)", MsrcWindowsProduct.ProductName(host with { WindowsInstallationType = "Server Core" }));
    }

    [TestMethod]
    public void HasProductReference_UsesScopedVendorRecordsAndPreservesAmbiguity()
    {
        var doc = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(MsrcCvrfReaderTests.Example), 10000, 100);
        var candidate = new MsrcCandidate(doc.Id, doc, doc.Advisories.Single());
        CollectionAssert.AreEqual(new[] { "Moderate" }, MsrcWindowsProduct.Severities(candidate, "Windows Server 2022 (Server Core installation)"));
        Assert.IsTrue(MsrcWindowsProduct.HasProductReference(candidate, "Windows Server 2022 (Server Core installation)"));
        Assert.IsTrue(MsrcWindowsProduct.VendorKnownAffected(candidate, "Windows Server 2022"));
        Assert.IsFalse(MsrcWindowsProduct.VendorKnownAffected(candidate, "Windows Server 2022 (Server Core installation)"));
        var mixed = candidate with { Advisory = candidate.Advisory with { ProductStatuses = [.. candidate.Advisory.ProductStatuses, new("11923", 2)] } };
        Assert.IsTrue(MsrcWindowsProduct.VendorKnownAffected(mixed, "Windows Server 2022"));
        Assert.IsTrue(MsrcWindowsProduct.HasOtherProductStatus(mixed, "Windows Server 2022"));
        Assert.IsFalse(MsrcWindowsProduct.HasProductReference(candidate, "Windows Server 2019"));
        Assert.IsNull(MsrcWindowsProduct.HasProductReference(candidate with { Advisory = candidate.Advisory with { HasUnscopedStatus = true } }, "Windows Server 2022"));
        var ambiguous = doc with { AmbiguousProductIds = new HashSet<string> { "11923" } };
        Assert.IsEmpty(MsrcWindowsProduct.Severities(candidate with { Document = ambiguous }, "Windows Server 2022"));
        Assert.IsFalse(MsrcWindowsProduct.VendorKnownAffected(candidate with { Document = ambiguous }, "Windows Server 2022"));
        Assert.IsNull(MsrcWindowsProduct.HasProductReference(candidate with { Document = ambiguous }, "Windows Server 2022"));
        Assert.IsNull(MsrcWindowsProduct.HasProductReference(candidate with { Document = ambiguous }, "Windows Server 2019"));
    }

    [TestMethod]
    public void ProductDiscovery_FindsCvesWithoutAgentCveInputAndMarksGaps()
    {
        var doc = MsrcCvrfReader.Read(Encoding.UTF8.GetBytes(MsrcCvrfReaderTests.Example), 10000, 100);
        var advisory = doc.Advisories.Single();
        var result = MsrcProductDiscovery.Discover(doc);
        Assert.IsTrue(result.Complete);
        Assert.Contains(new MsrcProductCandidate("Windows Server 2022 (Server Core installation)", advisory.Cve), result.Candidates);

        var unscoped = doc with { Advisories = [advisory with { HasUnscopedStatus = true }] };
        Assert.IsFalse(MsrcProductDiscovery.Discover(unscoped).Complete);
        var duplicate = doc with { DuplicateAdvisoryIds = new HashSet<string> { advisory.Cve } };
        Assert.IsFalse(MsrcProductDiscovery.Discover(duplicate).Complete);
        var noScope = doc with { Advisories = [advisory with { ProductStatuses = [], Remediations = [] }] };
        Assert.IsFalse(MsrcProductDiscovery.Discover(noScope).Complete);
        var ambiguous = doc with { AmbiguousProductIds = new HashSet<string> { "11923" } };
        Assert.IsFalse(MsrcProductDiscovery.Discover(ambiguous).Complete);
        Assert.IsFalse(MsrcProductDiscovery.Discover(doc with { HasVulnerabilitySection = false }).Complete);
    }
}
