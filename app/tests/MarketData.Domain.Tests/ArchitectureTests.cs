namespace MarketData.Domain.Tests;

public sealed class ArchitectureTests
{
    /// <summary>
    /// Walks up from the test binary to the solution root. .NET 10 emits the XML
    /// <c>.slnx</c> format by default, so both extensions are accepted rather
    /// than pinning this to one.
    /// </summary>
    private static string DomainCsprojPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "MarketData.Domain", "MarketData.Domain.csproj");
    }

    [Fact]
    public void Domain_project_has_no_package_references()
    {
        var csproj = File.ReadAllText(DomainCsprojPath());

        Assert.DoesNotContain("PackageReference", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Domain_project_has_no_project_references()
    {
        var csproj = File.ReadAllText(DomainCsprojPath());

        Assert.DoesNotContain("ProjectReference", csproj, StringComparison.Ordinal);
    }
}
