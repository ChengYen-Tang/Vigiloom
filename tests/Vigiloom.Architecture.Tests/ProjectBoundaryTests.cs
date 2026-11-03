using System.Xml.Linq;

namespace Vigiloom.Architecture.Tests;

[TestClass]
public sealed class ProjectBoundaryTests
{
    private static readonly string Root = FindRepositoryRoot();
    private static readonly string[] ModuleNames =
    [
        "IdentityAccess", "Resources", "NetworkMonitoring", "AgentManagement",
        "ServiceObservability", "SecurityAnalysis", "Publishing", "AlertingOperations"
    ];
    private static readonly string[] TestHelpers = ["Vigiloom.Testing.SqlServer", "Vigiloom.Testing.AgentHost"];

    [TestMethod]
    public void Solution_ContainsEverySourceAndTestProject()
    {
        var projects = ReadProjects();
        var solution = XDocument.Load(Path.Combine(Root, "Vigiloom.slnx"));
        var included = solution.Descendants("Project")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Path")!.Value))
            .ToArray();

        Assert.HasCount(projects.Count, included);
        Assert.IsEmpty(projects.Keys.Except(included), "Projects missing from the solution.");
        Assert.IsEmpty(included.Except(projects.Keys), "Stale projects in the solution.");
    }

    [TestMethod]
    public void ProductionReferences_StayWithinDocumentedBoundaries()
    {
        var projects = ReadProjects();
        var moduleNames = ModuleNames.Select(name => $"Vigiloom.Modules.{name}").ToArray();

        foreach (var project in projects.Values.Where(project => !project.Name.EndsWith(".Tests", StringComparison.Ordinal)
            && !TestHelpers.Contains(project.Name, StringComparer.Ordinal)))
        {
            var allowed = AllowedReferences(project.Name, moduleNames);
            Assert.IsNotNull(allowed, $"Undocumented production project: {project.Name}");
            Assert.IsEmpty(project.References.Except(allowed),
                $"Forbidden dependency in {project.Name}.");
            Assert.IsEmpty(project.References.Intersect(TestHelpers),
                $"Production project {project.Name} cannot reference test helpers.");
        }

        Assert.IsEmpty(moduleNames.Except(projects.Keys), "A planned module is missing.");
        Assert.DoesNotContain("Vigiloom.Agent", projects["Vigiloom.Updater"].References);
        Assert.DoesNotContain("Vigiloom.Updater", projects["Vigiloom.Agent"].References);
        CollectionAssert.AreEquivalent(new[] { "Vigiloom.Agent" }, projects["Vigiloom.Testing.AgentHost"].References);
    }

    [TestMethod]
    public void ProjectGraph_HasNoMissingTargetsOrCycles()
    {
        var projects = ReadProjects();
        var complete = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in projects.Keys)
        {
            Visit(name);
        }

        void Visit(string name)
        {
            Assert.Contains(name, projects.Keys, $"Unknown reference: {name}");
            if (complete.Contains(name))
            {
                return;
            }

            Assert.IsTrue(active.Add(name), $"Circular project reference through {name}.");
            foreach (var dependency in projects[name].References)
            {
                Visit(dependency);
            }

            active.Remove(name);
            complete.Add(name);
        }
    }

    [TestMethod]
    public void Tests_UseMSTest_AndReferenceTheirProductionProject()
    {
        var projects = ReadProjects();
        var testProps = XDocument.Load(Path.Combine(Root, "tests", "Directory.Build.props"));
        Assert.Contains("MSTest", testProps.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")!.Value).ToArray());

        foreach (var project in projects.Values.Where(project => project.Name.EndsWith(".Tests", StringComparison.Ordinal)))
        {
            if (project.Name != "Vigiloom.Architecture.Tests")
            {
                Assert.Contains(project.Name[..^".Tests".Length], project.References,
                    $"Missing production reference in {project.Name}.");
            }

            Assert.IsEmpty(project.Document.Descendants("PackageReference")
                .Where(element => element.Attribute("Version") is not null),
                "Package versions must be managed centrally.");
        }

        var forbidden = new[] { "xunit", "nunit", "tunit" };
        var buildFiles = projects.Values.Select(project => project.Document)
            .Append(testProps)
            .Append(XDocument.Load(Path.Combine(Root, "Directory.Packages.props")));
        Assert.IsEmpty(buildFiles.SelectMany(file => file.Descendants())
            .Where(element => element.Name.LocalName is "PackageReference" or "PackageVersion")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .Where(package => forbidden.Any(name => package.Contains(name, StringComparison.OrdinalIgnoreCase))),
            "All test projects must use MSTest.");
    }

    [TestMethod]
    public void BrowserProject_CannotReferenceServerPackages_AndUsesNet10()
    {
        var projects = ReadProjects();
        var client = projects["Vigiloom.Web.Client"];
        Assert.HasCount(1, client.References);
        Assert.Contains("Vigiloom.Contracts", client.References);
        Assert.IsEmpty(client.Document.Descendants("PackageReference")
            .Select(element => element.Attribute("Include")!.Value)
            .Where(package => package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || package.Contains("SqlClient", StringComparison.Ordinal)
                || package.Contains("Hosting.WindowsServices", StringComparison.Ordinal)));

        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        Assert.AreEqual("net10.0", props.Descendants("TargetFramework").Single().Value);
        Assert.IsEmpty(projects.Values.SelectMany(project => project.Document.Descendants("TargetFramework"))
            .Where(element => element.Value != "net10.0"));
    }

    private static string[]? AllowedReferences(string name, string[] modules) => name switch
    {
        "Vigiloom.BuildingBlocks" or "Vigiloom.Contracts" => [],
        "Vigiloom.Infrastructure" => ["Vigiloom.BuildingBlocks"],
        "Vigiloom.Web.Client" => ["Vigiloom.Contracts"],
        "Vigiloom.Web" => [.. modules, "Vigiloom.Infrastructure", "Vigiloom.Web.Client"],
        "Vigiloom.Worker" => [.. modules, "Vigiloom.Infrastructure"],
        "Vigiloom.Migrator" => ["Vigiloom.Infrastructure", "Vigiloom.Modules.IdentityAccess", "Vigiloom.Modules.Resources", "Vigiloom.Modules.NetworkMonitoring", "Vigiloom.Modules.AgentManagement", "Vigiloom.Modules.ServiceObservability", "Vigiloom.Modules.AlertingOperations"],
        "Vigiloom.SpeedLab" => ["Vigiloom.Modules.NetworkMonitoring"],
        "Vigiloom.Agent.Core" or "Vigiloom.Updater.Core" => ["Vigiloom.BuildingBlocks", "Vigiloom.Contracts"],
        "Vigiloom.Agent.Platforms.Windows" or "Vigiloom.Agent.Platforms.Linux" => ["Vigiloom.Agent.Core"],
        "Vigiloom.Agent" => ["Vigiloom.Agent.Core", "Vigiloom.Agent.Platforms.Windows", "Vigiloom.Agent.Platforms.Linux"],
        "Vigiloom.Updater" => ["Vigiloom.Updater.Core"],
        _ when modules.Contains(name) => ["Vigiloom.BuildingBlocks", "Vigiloom.Contracts", "Vigiloom.Infrastructure"],
        _ => null
    };

    private static Dictionary<string, ProjectDescription> ReadProjects() =>
        new[] { "src", "tests", "tools" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(Root, folder), "*.csproj", SearchOption.AllDirectories))
            .Select(path =>
            {
                var document = XDocument.Load(path);
                return new ProjectDescription(Path.GetFileNameWithoutExtension(path), document,
                    document.Descendants("ProjectReference")
                        .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")!.Value))
                        .ToArray());
            })
            .ToDictionary(project => project.Name, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vigiloom.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Vigiloom.slnx was not found above the test output.");
    }

    private sealed record ProjectDescription(string Name, XDocument Document, string[] References);
}
