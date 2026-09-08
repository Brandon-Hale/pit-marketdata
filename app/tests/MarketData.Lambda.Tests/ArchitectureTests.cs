using System.Xml.Linq;

namespace MarketData.Lambda.Tests;

public sealed class ArchitectureTests
{
    private static DirectoryInfo SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.slnx")) &&
               !File.Exists(Path.Combine(dir.FullName, "MarketData.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>
    /// The Lambda must not carry DuckDB, directly or transitively. Keeping it out is the
    /// entire reason the cursor holds recent closes; a transitive reference would add
    /// ~60MB and an httpfs download at cold start without anyone noticing.
    /// </summary>
    [Fact]
    public void Lambda_does_not_reference_duckdb_transitively()
    {
        var offenders = PackagesMatching(
            Path.Combine(SolutionRoot().FullName, "src", "MarketData.Lambda", "MarketData.Lambda.csproj"),
            "DuckDB");

        Assert.True(
            offenders.Count == 0,
            "DuckDB reached MarketData.Lambda via: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Proves the guard above can fail. MarketData.Query does reference DuckDB, so walking
    /// from there must find it — otherwise a passing Lambda test would prove nothing.
    /// </summary>
    [Fact]
    public void The_guard_detects_duckdb_where_it_genuinely_is()
    {
        var offenders = PackagesMatching(
            Path.Combine(SolutionRoot().FullName, "src", "MarketData.Query", "MarketData.Query.csproj"),
            "DuckDB");

        Assert.Contains("MarketData.Query", offenders);
    }

    private static List<string> PackagesMatching(string csprojPath, string needle)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offenders = new List<string>();

        Walk(csprojPath);

        return offenders;

        void Walk(string csproj)
        {
            var full = Path.GetFullPath(csproj);
            if (!visited.Add(full) || !File.Exists(full))
            {
                return;
            }

            var document = XDocument.Load(full);

            if (document.Descendants("PackageReference")
                .Any(p => (p.Attribute("Include")?.Value ?? string.Empty)
                    .Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                offenders.Add(Path.GetFileNameWithoutExtension(full));
            }

            var directory = Path.GetDirectoryName(full)!;
            foreach (var reference in document.Descendants("ProjectReference"))
            {
                if (reference.Attribute("Include")?.Value is { } include)
                {
                    Walk(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                }
            }
        }
    }
}
