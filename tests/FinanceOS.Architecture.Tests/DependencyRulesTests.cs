using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Xunit;

namespace FinanceOS.Architecture.Tests;

/// <summary>Enforces the layer rules from ARCHITECTURE.md §4.1.</summary>
public sealed class DependencyRulesTests
{
    public static TheoryData<string, string[]> AllowedProjectReferences => new()
    {
        { "FinanceOS.Core", [] },
        { "FinanceOS.Data", ["FinanceOS.Core"] },
        { "FinanceOS.UI", [] },
        { "FinanceOS.Platform.Windows", ["FinanceOS.Core"] },
        { "FinanceOS.Integrations", ["FinanceOS.Core"] },
    };

    [Theory]
    [MemberData(nameof(AllowedProjectReferences))]
    public void Project_references_only_allowed_projects(string project, string[] allowed)
    {
        var references = LoadProject(project)
            .Descendants("ProjectReference")
            .Select(r => Path.GetFileNameWithoutExtension((string)r.Attribute("Include")!));

        Assert.All(references, r => Assert.Contains(r, allowed));
    }

    [Fact]
    public void Core_is_plain_dotnet_without_ui_persistence_or_windows_dependencies()
    {
        var csproj = LoadProject("FinanceOS.Core");
        Assert.Equal("net10.0", csproj.Descendants("TargetFramework").Single().Value);
        Assert.Empty(csproj.Descendants("PackageReference"));
        Assert.Empty(csproj.Descendants("FrameworkReference"));
        Assert.Empty(csproj.Descendants("UseWPF"));

        string[] forbidden =
        [
            "PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows",
            "Microsoft.Data.Sqlite", "Dapper", "Microsoft.Toolkit", "Microsoft.Windows.SDK", "FinanceOS.",
        ];
        var referenced = typeof(Core.AssemblyMarker).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
        Assert.DoesNotContain(referenced, name => forbidden.Any(f => name.StartsWith(f, StringComparison.Ordinal)));
    }

    [Fact]
    public void Data_and_Integrations_do_not_reference_wpf()
    {
        Assembly[] assemblies = [typeof(Data.AssemblyMarker).Assembly, typeof(Integrations.AssemblyMarker).Assembly];
        foreach (var assembly in assemblies)
        {
            var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");
            Assert.DoesNotContain(referenced, name => name is "PresentationFramework" or "PresentationCore" or "WindowsBase");
        }
    }

    private static XDocument LoadProject(string name) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", name, $"{name}.csproj"));

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FinanceOS.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("FinanceOS.slnx not found above " + AppContext.BaseDirectory);
    }
}
