using Factory.Engine.Workspace;

namespace Factory.Tests;

public sealed class RazorComponentsTests : IDisposable
{
    private const string DeviceCard = """
        @typeparam TItem where TItem : class
        @inherits OwningComponentBase
        @inject NavigationManager Nav
        @using Demo.Models

        <div class="card @Css" @onclick="() => OnSelected.InvokeAsync(Item)">
            <h3>@Title</h3>
            @ChildContent
        </div>

        @code {
            [Parameter, EditorRequired] public TItem Item { get; set; } = default!;
            [Parameter] public string Title { get; set; } = "";

            [Parameter]
            public EventCallback<TItem> OnSelected { get; set; }

            [EditorRequired]
            [Parameter]
            public RenderFragment<Dictionary<string, int>>?   ChildContent { get; set; }

            [CascadingParameter(Name = "Theme")] private Theme? Theme { get; set; }
            [Parameter(CaptureUnmatchedValues = true)] public IDictionary<string, object>? Attributes { get; set; }

            private string Css { get; set; } = "plain";
            public int Clicks { get; private set; }

            private void Reset() { Clicks = 0; }
        }
        """;

    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void A_component_is_described_by_its_directives_and_parameters_without_its_markup()
    {
        var described = RazorComponents.Describe("src/Web/Components/DeviceCard.razor", DeviceCard, hasCodeBehind: false, maxChars: 3000);

        Assert.Equal("""
            // The Razor component DeviceCard, used as <DeviceCard ... />. Its directives and parameters, without its markup.
            @typeparam TItem where TItem : class
            @inherits OwningComponentBase
            [Parameter, EditorRequired] public TItem Item { get; set; }
            [Parameter] public string Title { get; set; }
            [Parameter] public EventCallback<TItem> OnSelected { get; set; }
            [EditorRequired] [Parameter] public RenderFragment<Dictionary<string, int>>? ChildContent { get; set; }
            [CascadingParameter(Name = "Theme")] private Theme? Theme { get; set; }
            [Parameter(CaptureUnmatchedValues = true)] public IDictionary<string, object>? Attributes { get; set; }

            """.ReplaceLineEndings("\n"), described);
        Assert.Equal(described, RazorComponents.Describe("src/Web/Components/DeviceCard.razor", DeviceCard.ReplaceLineEndings("\r\n"), false, 3000));
    }

    [Fact]
    public void A_component_with_no_parameters_says_so_and_one_with_a_code_behind_points_at_it()
    {
        Assert.Contains("// It has no parameters.", RazorComponents.Describe("Pages/Home.razor", "@page \"/\"\n<h1>Hello</h1>\n", false, 3000));
        Assert.Contains("@page \"/\"", RazorComponents.Describe("Pages/Home.razor", "@page \"/\"\n<h1>Hello</h1>\n", false, 3000));

        var withCodeBehind = RazorComponents.Describe("Pages/Home.razor", "<h1>Hello</h1>\n", hasCodeBehind: true, 3000);
        Assert.Contains("// It declares no parameters in this file.", withCodeBehind);
        Assert.Contains("// The rest of the class is in Home.razor.cs, which may declare more.", withCodeBehind);
    }

    [Fact]
    public void A_long_list_of_parameters_is_cut_to_the_limit_and_says_how_many_are_left()
    {
        var content = "@code {\n" + string.Concat(Enumerable.Range(0, 50).Select(i => $"    [Parameter] public string Value{i} {{ get; set; }}\n")) + "}\n";

        var described = RazorComponents.Describe("Big.razor", content, false, 600);

        Assert.True(described.Length < 700);
        Assert.Contains("[Parameter] public string Value0 { get; set; }", described);
        Assert.DoesNotContain("Value49", described);
        Assert.Matches(@"// … \d+ more not shown", described);
    }

    [Theory]
    [InlineData("src/Web/DeviceCard.razor", "DeviceCard")]
    [InlineData("DeviceCard.RAZOR", "DeviceCard")]
    [InlineData("src/Web/_Imports.razor", null)]
    [InlineData("src/Web/deviceCard.razor", null)]
    [InlineData("src/Web/DeviceCard.razor.cs", null)]
    [InlineData("src/Web/DeviceCard.cs", null)]
    public void A_component_is_named_after_its_file(string path, string? expected) => Assert.Equal(expected, RazorComponents.NameOf(path));

    [Fact]
    public void A_component_the_text_names_is_found_by_its_file_and_kept_apart_from_source_definitions()
    {
        _repo.Write("src/Web/Components/DeviceCard.razor", DeviceCard);
        _repo.Write("src/Web/Components/_Imports.razor", "@using Demo\n");
        _repo.Write("src/Web/Pages/Devices.razor", "@page \"/devices\"\n<DeviceCard Title=\"x\" />\n");
        _repo.Write("src/Web/Models/Device.cs", "public sealed class Device { }\n");
        _repo.Write("src/Web/obj/Debug/Generated.razor", "<p>generated</p>\n");

        const string text = "Show each Device in a DeviceCard on the Devices page. See <DeviceCard Title=\"x\" />.";

        Assert.Equal(["src/Web/Components/DeviceCard.razor", "src/Web/Pages/Devices.razor"], DefinitionFinder.FindComponents(_repo.Path, text, [], 10));
        // The component's file isn't handed over whole as a definition, and a class isn't offered as a component.
        Assert.Equal(["src/Web/Models/Device.cs"], DefinitionFinder.Find(_repo.Path, text, [], 10));
        // A file the agent already has isn't described to it again.
        Assert.Equal(["src/Web/Pages/Devices.razor"], DefinitionFinder.FindComponents(_repo.Path, text, ["src/Web/Components/DeviceCard.razor"], 10));
        Assert.Equal(["src/Web/Components/DeviceCard.razor"], DefinitionFinder.FindComponents(_repo.Path, text, [], 1));
        // The repository's own, so a package or framework type of the same name isn't looked up in its place.
        Assert.Contains("DeviceCard", DefinitionFinder.DeclaredNames(_repo.Path));
        Assert.DoesNotContain("_Imports", DefinitionFinder.DeclaredNames(_repo.Path));
        Assert.DoesNotContain("Generated", DefinitionFinder.DeclaredNames(_repo.Path));
    }
}
