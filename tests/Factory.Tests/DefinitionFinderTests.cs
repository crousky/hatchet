using Factory.Engine.Workspace;

namespace Factory.Tests;

public class DefinitionFinderTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public DefinitionFinderTests()
    {
        _repo.Write("src/Core/Scanning/IScanOptionsProvider.cs", "namespace Core;\npublic interface IScanOptionsProvider { ScanOptions Current { get; } }\n");
        _repo.Write("src/Core/Scanning/ScanTarget.cs", "namespace Core;\npublic sealed record ScanTarget(int Index);\npublic enum ScanState { Idle }\n");
        _repo.Write("src/Discovery/Arp/NeighborEntry.cs", "public class NeighborEntry { public NeighborEntry(int state, uint interfaceIndex) { } }\n");
        _repo.Write("web/src/graph.ts", "export interface GraphNode { id: string }\nexport type EdgeKind = 'a' | 'b';\n");
        _repo.Write("src/Core/bin/Debug/Copy.cs", "public class NeighborEntry { }\n");
        _repo.Write("README.md", "class ScanTarget is documented here\n");
    }

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Finds_the_files_that_declare_the_types_a_text_names_in_the_order_it_names_them()
    {
        var files = DefinitionFinder.Find(_repo.Path,
            "error CS1061: 'IScanOptionsProvider' does not contain a definition for 'GetOptions'. ScanTarget.ScanTarget(NeighborEntry)",
            [], 10);

        Assert.Equal(["src/Core/Scanning/IScanOptionsProvider.cs", "src/Core/Scanning/ScanTarget.cs", "src/Discovery/Arp/NeighborEntry.cs"], files);
    }

    [Fact]
    public void Finds_records_enums_and_typescript_declarations()
    {
        Assert.Equal(["src/Core/Scanning/ScanTarget.cs"], DefinitionFinder.Find(_repo.Path, "Set ScanState to Idle.", [], 10));
        Assert.Equal(["web/src/graph.ts"], DefinitionFinder.Find(_repo.Path, "Each GraphNode has an EdgeKind.", [], 10));
    }

    [Fact]
    public void Leaves_out_files_the_agent_already_has_and_build_output()
    {
        var files = DefinitionFinder.Find(_repo.Path, "Use ScanTarget and NeighborEntry.", ["src\\Core\\Scanning\\ScanTarget.cs"], 10);

        Assert.Equal(["src/Discovery/Arp/NeighborEntry.cs"], files);
    }

    [Fact]
    public void Ignores_names_nothing_declares_and_stops_at_the_limit()
    {
        Assert.Empty(DefinitionFinder.Find(_repo.Path, "Create the HostDiscoverer. Use ILogger and Task.", [], 10));
        Assert.Single(DefinitionFinder.Find(_repo.Path, "ScanTarget, NeighborEntry and IScanOptionsProvider", [], 1));
    }
}
